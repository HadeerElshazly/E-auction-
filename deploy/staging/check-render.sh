#!/usr/bin/env bash
#
# Asserts the things the staging namespace will refuse, before Helm is let near
# it. Every check here corresponds to a limit in the owner's access note, and
# each one fails as a rejection rather than as degraded behaviour — which is
# why they are worth catching in CI rather than reading about in an event log.
#
#   usage: deploy/staging/check-render.sh <rendered.yaml> [more.yaml ...]
set -euo pipefail

[ $# -ge 1 ] || { echo "usage: $0 <rendered.yaml> [...]" >&2; exit 2; }

python3 - "$@" <<'PY'
import sys, io, re, yaml

ok = True
def bad(msg):
    global ok
    ok = False
    print(f"  FAIL  {msg}")

CLUSTER_SCOPED = {
    'ClusterRole', 'ClusterRoleBinding', 'CustomResourceDefinition', 'StorageClass',
    'IngressClass', 'MutatingWebhookConfiguration', 'ValidatingWebhookConfiguration',
    'Namespace', 'PersistentVolume', 'APIService', 'PriorityClass',
}

# The quota, from the access note.
MAX_REQ_CPU, MAX_LIM_CPU, MAX_NODEPORTS = 3000, 6000, 5

def cpu(v):
    if v is None: return 0
    v = str(v)
    return int(v[:-1]) if v.endswith('m') else int(float(v) * 1000)

def workload_containers(d):
    spec = d.get('spec', {})
    tmpl = spec.get('template', {}).get('spec', {})
    return tmpl.get('containers', []) + tmpl.get('initContainers', []), tmpl

docs, raw_all = [], ''
for path in sys.argv[1:]:
    raw = io.open(path, encoding='utf-8').read()
    raw_all += raw
    docs += [d for d in yaml.safe_load_all(raw) if d]

print(f"checking {len(docs)} objects from {len(sys.argv) - 1} file(s)")

kinds = {d.get('kind') for d in docs}

# 1. Nothing cluster-scoped: the token is namespace-admin only, and a chart that
#    renders one of these fails the whole release with a Forbidden.
if kinds & CLUSTER_SCOPED:
    bad(f"cluster-scoped objects: {sorted(kinds & CLUSTER_SCOPED)}")

# 2. No Ingress. The only IngressClass on the node belongs to the other tenant's
#    router and must not be borrowed.
if 'Ingress' in kinds:
    bad("an Ingress was rendered; the only IngressClass belongs to the archive")

# 3. No LoadBalancer: the quota allows zero, and k3s would try to allocate one.
lb = [d['metadata']['name'] for d in docs
      if d.get('kind') == 'Service' and d['spec'].get('type') == 'LoadBalancer']
if lb:
    bad(f"LoadBalancer services (quota allows 0): {lb}")

# 4. At most five NodePort services.
np = [(d['metadata']['name'], p.get('nodePort'))
      for d in docs if d.get('kind') == 'Service' and d['spec'].get('type') == 'NodePort'
      for p in d['spec'].get('ports', [])]
if len(np) > MAX_NODEPORTS:
    bad(f"{len(np)} NodePorts, quota allows {MAX_NODEPORTS}: {np}")

# 5. The archive's two ports are taken.
for name, port in np:
    if port in (31831, 31832):
        bad(f"{name} wants nodePort {port}, which belongs to the archive")

# 6. Every container declares a CPU limit. Without one the LimitRange grants a
#    full core, and a dozen of those exceed the namespace cap on their own — the
#    pods then never schedule, which reads like a broken image rather than a
#    quota problem.
for d in docs:
    if d.get('kind') in ('Deployment', 'StatefulSet', 'Job', 'DaemonSet'):
        cs, _ = workload_containers(d)
        for c in cs:
            lim = c.get('resources', {}).get('limits', {})
            if not lim.get('cpu'):
                bad(f"{d['metadata']['name']}/{c['name']}: no CPU limit")
            if not lim.get('memory'):
                bad(f"{d['metadata']['name']}/{c['name']}: no memory limit")

# 7. A PodDisruptionBudget over a single replica can never be satisfied, so it
#    blocks `kubectl drain` on a node we share. Hostile, and hard to diagnose
#    from the other side.
for d in docs:
    if d.get('kind') == 'PodDisruptionBudget':
        bad(f"{d['metadata']['name']}: a PDB on a single-replica demo blocks node drains")

# 8. An autoscaler with minReplicas above one silently contradicts the budget.
for d in docs:
    if d.get('kind') == 'HorizontalPodAutoscaler':
        bad(f"{d['metadata']['name']}: autoscaling is not budgeted for here")

# 9. The bid processor is not sharded across instances: a second replica drives
#    every auction in parallel and publishes duplicate winners.
for d in docs:
    if d.get('kind') == 'Deployment' and d['metadata']['name'].endswith('bid-processor'):
        if d['spec'].get('replicas', 1) != 1:
            bad(f"bid-processor has {d['spec'].get('replicas')} replicas; it must be 1")

# 10. Pod Security `baseline` is enforced.
for d in docs:
    if d.get('kind') in ('Deployment', 'StatefulSet', 'Job', 'DaemonSet'):
        cs, tmpl = workload_containers(d)
        n = d['metadata']['name']
        for field in ('hostNetwork', 'hostPID', 'hostIPC'):
            if tmpl.get(field):
                bad(f"{n}: {field} is refused by Pod Security baseline")
        for v in tmpl.get('volumes', []):
            if 'hostPath' in v:
                bad(f"{n}: hostPath is refused by Pod Security baseline")
        for c in cs:
            if c.get('securityContext', {}).get('privileged'):
                bad(f"{n}/{c['name']}: privileged is refused")
            for p in c.get('ports', []) or []:
                if p.get('hostPort'):
                    bad(f"{n}/{c['name']}: hostPort is refused")

# 11. No credential may be rendered. Secrets are referenced, never templated.
for label, pattern in (
    ('a private key', r'BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY'),
    ('a JWT', r'\beyJ[A-Za-z0-9_-]{20,}\.'),
    ('an inline password', r'(?<!secretKey)(?<!secretName)\bpassword["\']?\s*[:=]\s*["\']?[^\s"\'{}$]{6,}'),
):
    m = re.search(pattern, raw_all, re.I)
    if m:
        bad(f"{label} appears in the rendered output near: {m.group(0)[:40]!r}")

# --- the budget -------------------------------------------------------------
req = lim = 0
for d in docs:
    if d.get('kind') in ('Deployment', 'StatefulSet', 'DaemonSet', 'Job'):
        n = d['spec'].get('replicas', 1) if d['kind'] in ('Deployment', 'StatefulSet') else 1
        cs, _ = workload_containers(d)
        for c in cs:
            r = c.get('resources', {})
            req += cpu(r.get('requests', {}).get('cpu')) * n
            lim += cpu(r.get('limits', {}).get('cpu')) * n

print(f"  CPU requests {req}m / {MAX_REQ_CPU}m     limits {lim}m / {MAX_LIM_CPU}m")
print(f"  NodePorts    {len(np)} / {MAX_NODEPORTS}  {np}")
if req > MAX_REQ_CPU: bad(f"CPU requests {req}m exceed the {MAX_REQ_CPU}m quota")
if lim > MAX_LIM_CPU: bad(f"CPU limits {lim}m exceed the {MAX_LIM_CPU}m quota")

print()
print("all checks passed" if ok else "CHECKS FAILED")
sys.exit(0 if ok else 1)
PY
