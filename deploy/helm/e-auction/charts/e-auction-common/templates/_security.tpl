{{/*
The one place the two Kubernetes flavours genuinely differ.

OpenShift's restricted-v2 SCC assigns each namespace a UID range and admits a
pod only if it does not ask for a UID outside it. A chart that hard-codes
runAsUser therefore fails to schedule on OpenShift — the pod is rejected
outright, not downgraded.

So on OpenShift we state the requirement (runAsNonRoot) and let the platform
pick the number. On plain Kubernetes nothing assigns one, so we must.

Both paths are equally locked down otherwise: no privilege escalation, every
capability dropped, read-only root filesystem, RuntimeDefault seccomp. The
images already run as a non-root user and write nothing outside /tmp, which is
mounted as an emptyDir, so an arbitrary assigned UID works.
*/}}

{{- define "e-auction.podSecurityContext" -}}
runAsNonRoot: true
seccompProfile:
  type: RuntimeDefault
{{- if ne .root.Values.platform "openshift" }}
runAsUser: {{ .root.Values.security.runAsUser }}
runAsGroup: {{ .root.Values.security.runAsGroup }}
fsGroup: {{ .root.Values.security.fsGroup }}
{{- end }}
{{- end -}}

{{- define "e-auction.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop:
    - ALL
{{- end -}}
