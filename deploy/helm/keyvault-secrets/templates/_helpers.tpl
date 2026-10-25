{{- define "kvs.labels" -}}
app.kubernetes.io/name: keyvault-secrets
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "kvs.selector" -}}
app.kubernetes.io/name: keyvault-secrets
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}