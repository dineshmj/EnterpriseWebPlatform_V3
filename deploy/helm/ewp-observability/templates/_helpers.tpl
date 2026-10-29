{{- define "obs.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: ewp-observability
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end -}}

{{- define "obs.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{/* SEC: every container - read-only root, no privilege escalation, no capabilities. */}}
{{- define "obs.containerSecurity" -}}
readOnlyRootFilesystem: true
allowPrivilegeEscalation: false
capabilities:
  drop: ["ALL"]
{{- end -}}

{{/* SEC: every pod - no Kubernetes API token (none calls the API), non-root, default seccomp. */}}
{{- define "obs.podSecurity" -}}
automountServiceAccountToken: false
enableServiceLinks: false
securityContext:
  runAsNonRoot: true
  runAsUser: {{ .uid }}
  runAsGroup: {{ .uid }}
  fsGroup: {{ .uid }}
  seccompProfile:
    type: RuntimeDefault
{{- end -}}

{{/* A Service's address inside the namespace: (list $ "loki") -> loki.<namespace>.svc.cluster.local */}}
{{- define "obs.fqdn" -}}
{{ index . 1 }}.{{ (index . 0).Release.Namespace }}.svc.cluster.local
{{- end -}}

{{/* The certificate of a TLS-serving component (EWP's private CA), as the EWP chart issues them. */}}
{{- define "obs.certificate" -}}
apiVersion: cert-manager.io/v1
kind: Certificate
metadata:
  name: {{ .name }}-tls
  labels:
    {{- include "obs.labels" . | nindent 4 }}
spec:
  secretName: {{ .name }}-tls
  duration: 2160h
  renewBefore: 720h
  privateKey:
    algorithm: ECDSA
    size: 256
    rotationPolicy: Always
  usages:
    - server auth
    - digital signature
  dnsNames:
    - {{ .name }}
    - {{ .name }}.{{ .root.Release.Namespace }}
    - {{ .name }}.{{ .root.Release.Namespace }}.svc
    - {{ .name }}.{{ .root.Release.Namespace }}.svc.cluster.local
  issuerRef:
    {{- toYaml .root.Values.tls.issuerRef | nindent 4 }}
{{- end -}}