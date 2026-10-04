{{/*
External exposure — the second place the flavours differ.

OpenShift has no Ingress controller by default; it has Routes, served by its
own router. Plain Kubernetes has no Route type at all, so a chart that ships
one is rejected by the API server before anything runs.

Same intent, two objects. The values are identical either way, so a client
switching platforms changes `platform` and nothing else.
*/}}

{{- define "e-auction.expose" -}}
{{- if .svc.expose.enabled -}}
{{- if eq .root.Values.platform "openshift" -}}
apiVersion: route.openshift.io/v1
kind: Route
metadata:
  name: {{ include "e-auction.fullname" . }}
  labels:
    {{- include "e-auction.labels" . | nindent 4 }}
  {{- with .svc.expose.annotations }}
  annotations:
    {{- toYaml . | nindent 4 }}
  {{- end }}
spec:
  {{- with .svc.expose.host }}
  host: {{ . | quote }}
  {{- end }}
  path: {{ .svc.expose.path | quote }}
  to:
    kind: Service
    name: {{ include "e-auction.fullname" . }}
    weight: 100
  port:
    targetPort: http
  {{- if .svc.expose.tls.enabled }}
  tls:
    termination: edge
    insecureEdgeTerminationPolicy: Redirect
  {{- end }}
{{- else -}}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ include "e-auction.fullname" . }}
  labels:
    {{- include "e-auction.labels" . | nindent 4 }}
  {{- with .svc.expose.annotations }}
  annotations:
    {{- toYaml . | nindent 4 }}
  {{- end }}
spec:
  {{- with .root.Values.ingress.className }}
  ingressClassName: {{ . | quote }}
  {{- end }}
  {{- if and .svc.expose.tls.enabled .svc.expose.host }}
  tls:
    - hosts:
        - {{ .svc.expose.host | quote }}
      secretName: {{ .svc.expose.tls.secretName | default (printf "%s-tls" (include "e-auction.fullname" .)) }}
  {{- end }}
  rules:
    - {{ if .svc.expose.host }}host: {{ .svc.expose.host | quote }}
      {{ end }}http:
        paths:
          - path: {{ .svc.expose.path | quote }}
            pathType: Prefix
            backend:
              service:
                name: {{ include "e-auction.fullname" . }}
                port:
                  name: http
{{- end -}}
{{- end -}}
{{- end -}}
