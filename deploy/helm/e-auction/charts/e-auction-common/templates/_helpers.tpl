{{/*
Naming and labels. One place, so every object an operator sees carries the
same identity and a release can be selected or deleted as a unit.
*/}}

{{- define "e-auction.fullname" -}}
{{- printf "%s-%s" .root.Release.Name .name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "e-auction.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/version: {{ .root.Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
app.kubernetes.io/part-of: e-auction
{{- end -}}

{{- define "e-auction.selectorLabels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{/*
Image reference. The chart version is the deployment version, and appVersion
is the image tag, so a release is one coordinate rather than two.
*/}}
{{- define "e-auction.image" -}}
{{- $registry := .root.Values.global.imageRegistry -}}
{{- $tag := .root.Values.image.tag | default .root.Chart.AppVersion -}}
{{- if $registry -}}
{{ printf "%s/%s:%s" $registry .svc.image $tag }}
{{- else -}}
{{ printf "%s:%s" .svc.image $tag }}
{{- end -}}
{{- end -}}
