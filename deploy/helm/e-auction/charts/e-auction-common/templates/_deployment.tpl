{{/*
One deployment shape for every service. Takes: root, name, svc.
*/}}
{{- define "e-auction.deployment" -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ include "e-auction.fullname" . }}
  labels:
    {{- include "e-auction.labels" . | nindent 4 }}
spec:
  {{- if not .svc.autoscaling.enabled }}
  replicas: {{ .svc.replicas }}
  {{- end }}
  selector:
    matchLabels:
      {{- include "e-auction.selectorLabels" . | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "e-auction.selectorLabels" . | nindent 8 }}
      annotations:
        {{- /* Roll the pods when configuration changes, not only when the image does. */}}
        checksum/config: {{ toYaml .root.Values.config | sha256sum }}
        {{- with .svc.podAnnotations }}
        {{- toYaml . | nindent 8 }}
        {{- end }}
    spec:
      serviceAccountName: {{ .root.Release.Name }}-{{ .name }}
      {{- with .root.Values.global.imagePullSecrets }}
      imagePullSecrets:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      securityContext:
        {{- include "e-auction.podSecurityContext" . | nindent 8 }}
      containers:
        - name: {{ .name }}
          image: {{ include "e-auction.image" . }}
          imagePullPolicy: {{ .root.Values.image.pullPolicy }}
          securityContext:
            {{- include "e-auction.containerSecurityContext" . | nindent 12 }}
          {{- if .svc.port }}
          ports:
            - name: http
              containerPort: {{ .svc.port }}
              protocol: TCP
          {{- end }}
          env:
            {{- include "e-auction.commonEnv" . | nindent 12 }}
            {{- with .svc.env }}
            {{- toYaml . | nindent 12 }}
            {{- end }}
            {{- /*
              Secrets are referenced, never templated into values — a chart
              is committed to git and a connection string is not.
            */}}
            {{- range .svc.secretEnv }}
            - name: {{ .name }}
              valueFrom:
                secretKeyRef:
                  name: {{ .secretName }}
                  key: {{ .secretKey }}
            {{- end }}
          {{- if .svc.port }}
          livenessProbe:
            httpGet:
              path: /health/live
              port: http
            initialDelaySeconds: 10
            periodSeconds: 20
          readinessProbe:
            httpGet:
              path: /health/ready
              port: http
            initialDelaySeconds: 3
            periodSeconds: 5
          {{- end }}
          resources:
            {{- toYaml .svc.resources | nindent 12 }}
          volumeMounts:
            - name: tmp
              mountPath: /tmp
      volumes:
        {{- /*
          readOnlyRootFilesystem is on, and .NET still needs somewhere to
          write. An emptyDir also means an arbitrary OpenShift-assigned UID
          has a writable path without loosening the root filesystem.
        */}}
        - name: tmp
          emptyDir: {}
      {{- with .svc.nodeSelector }}
      nodeSelector:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      {{- with .svc.tolerations }}
      tolerations:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      {{- if .svc.spreadAcrossNodes }}
      topologySpreadConstraints:
        - maxSkew: 1
          topologyKey: kubernetes.io/hostname
          whenUnsatisfiable: ScheduleAnyway
          labelSelector:
            matchLabels:
              {{- include "e-auction.selectorLabels" . | nindent 14 }}
      {{- end }}
{{- end -}}

{{/*
Configuration every service shares. Env vars only, so Compose and Helm stay
one source of truth.
*/}}
{{- define "e-auction.commonEnv" -}}
- name: DOTNET_gcServer
  value: "1"
- name: TMPDIR
  value: /tmp
- name: Kafka__BootstrapServers
  value: {{ .root.Values.config.kafka.bootstrapServers | quote }}
- name: Jwt__Authority
  value: {{ .root.Values.config.jwt.authority | quote }}
- name: Jwt__Audience
  value: {{ .root.Values.config.jwt.audience | quote }}
{{- with .root.Values.config.cors.allowedOrigins }}
- name: Cors__AllowedOrigins
  value: {{ join "," . | quote }}
{{- end }}
{{- end -}}

{{- define "e-auction.serviceAccount" -}}
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ .root.Release.Name }}-{{ .name }}
  labels:
    {{- include "e-auction.labels" . | nindent 4 }}
automountServiceAccountToken: false
{{- end -}}

{{/*
The Service.

ClusterIP by default, because in a production install nothing reaches a service
except through the Ingress or Route above. A NodePort is the exception a shared
cluster forces: where the only IngressClass belongs to another tenant, a node
port is the one way out that does not touch their router. `service.nodePort`
pins the number so a firewall rule can be written once and stay true across
releases; left unset, the API server allocates one and it may move.
*/}}
{{- define "e-auction.service" -}}
{{- if .svc.port -}}
{{- $service := .svc.service | default dict -}}
{{- $type := $service.type | default "ClusterIP" -}}
apiVersion: v1
kind: Service
metadata:
  name: {{ include "e-auction.fullname" . }}
  labels:
    {{- include "e-auction.labels" . | nindent 4 }}
spec:
  type: {{ $type }}
  ports:
    - port: 80
      targetPort: http
      protocol: TCP
      name: http
      {{- if and (eq $type "NodePort") $service.nodePort }}
      nodePort: {{ $service.nodePort }}
      {{- end }}
  selector:
    {{- include "e-auction.selectorLabels" . | nindent 4 }}
{{- end -}}
{{- end -}}

{{/*
Horizontal scaling.

Reactive autoscaling is always late for an auction: load arrives in the last
thirty seconds of a hot lot and the pods appear after the spike. KEDA with a
cron trigger derived from auction end times is the real answer (§7.4); CPU
here is the floor, not the plan.
*/}}
{{- define "e-auction.hpa" -}}
{{- if .svc.autoscaling.enabled -}}
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: {{ include "e-auction.fullname" . }}
  labels:
    {{- include "e-auction.labels" . | nindent 4 }}
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: {{ include "e-auction.fullname" . }}
  minReplicas: {{ .svc.autoscaling.minReplicas }}
  maxReplicas: {{ .svc.autoscaling.maxReplicas }}
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: {{ .svc.autoscaling.targetCPUUtilizationPercentage }}
  behavior:
    scaleUp:
      stabilizationWindowSeconds: 0
      policies:
        - type: Percent
          value: 100
          periodSeconds: 15
    scaleDown:
      {{- /* Slow to scale down: a lull mid-auction is not the end of it. */}}
      stabilizationWindowSeconds: 300
{{- end -}}
{{- end -}}

{{- define "e-auction.pdb" -}}
{{- if .svc.podDisruptionBudget.enabled -}}
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: {{ include "e-auction.fullname" . }}
  labels:
    {{- include "e-auction.labels" . | nindent 4 }}
spec:
  minAvailable: {{ .svc.podDisruptionBudget.minAvailable }}
  selector:
    matchLabels:
      {{- include "e-auction.selectorLabels" . | nindent 6 }}
{{- end -}}
{{- end -}}
