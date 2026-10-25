{{/*
┌─ :: ARCHITECTURE :: One set of templates, 35 components ───────────────────────────────────────
│ Why : Every EWP service is deployed the same way (same security context, probes, TLS, network
│       rules); only its settings differ. One template per Kubernetes kind, ranging over
│       .Values.components, keeps that sameness reviewable in one place.
│ How : A component's key is its Service name and its image name without "ewp-". Services are
│       addressed by that short name (https://payments-api) inside the namespace - one EWP per
│       namespace. Setting values are templates (tpl), so they can name public addresses with
│       ewp.public and global values; secrets are only ever referenced, never rendered.
│ Ref : doc/Kubernetes.md | doc/Containers.md (run-time contract)
└────────────────────────────────────────────────────────────────────────────────────────────────
*/}}

{{/* The components that are switched on, as a dict. */}}
{{- define "ewp.enabled" -}}
{{- $out := dict -}}
{{- range $name, $c := .Values.components -}}
{{- if ne (toString $c.enabled) "false" -}}
{{- if or (ne (toString $c.simulator) "true") $.Values.global.simulators.enabled -}}
{{- $_ := set $out $name $c -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- toYaml $out -}}
{{- end -}}

{{/* "https" for every component but the .NET Kafka workers, which serve only probes on http 8080. */}}
{{- define "ewp.listener" -}}
{{- if and (eq .stack "dotnet") (eq .role "worker") -}}http{{- else -}}https{{- end -}}
{{- end -}}

{{/* The browser's address of a public component: (list $ "shell") -> https://shell.<domain> */}}
{{- define "ewp.public" -}}
{{- $root := index . 0 -}}
{{- $name := index . 1 -}}
{{- $c := index $root.Values.components $name | required (printf "ewp.public: no component %q" $name) -}}
{{- $host := $c.public.host | required (printf "ewp.public: component %q has no public.host" $name) -}}
https://{{ $host }}.{{ required "global.domain is required (e.g. demo.example.com)" $root.Values.global.domain }}
{{- end -}}

{{/* The browser's host name of a public component (no scheme). */}}
{{- define "ewp.publicHost" -}}
{{- trimPrefix "https://" (include "ewp.public" .) -}}
{{- end -}}

{{/* The address other pods use: (list $ "payments-api") -> https://payments-api */}}
{{- define "ewp.internal" -}}
{{- $root := index . 0 -}}
{{- $name := index . 1 -}}
{{- $c := index $root.Values.components $name | required (printf "ewp.internal: no component %q" $name) -}}
{{- if eq (include "ewp.listener" $c) "http" -}}http://{{ $name }}:8080{{- else -}}https://{{ $name }}{{- end -}}
{{- end -}}

{{/* repository:tag, or repository:tag@digest when global.image.digests names this component's
     image digest (the cloud demo pins every image to the digest its tag had at install time). */}}
{{- define "ewp.image" -}}
{{- $g := .root.Values.global.image -}}
{{- $tag := required "global.image.tag is required: the full commit SHA the images were built from" $g.tag -}}
{{- $digest := index ($g.digests | default dict) .name -}}
{{ $g.registry }}/ewp-{{ .name }}:{{ $tag }}{{ with $digest }}@{{ . }}{{ end }}
{{- end -}}

{{- define "ewp.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: ewp
app.kubernetes.io/version: {{ .root.Values.global.image.tag | default "unset" | trunc 63 | quote }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end -}}

{{- define "ewp.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{/* The Kubernetes Secret that holds a component's own secret settings. */}}
{{- define "ewp.secretName" -}}
{{ .root.Values.global.secretPrefix }}{{ .name }}
{{- end -}}

{{- define "ewp.probeGet" -}}
path: {{ .path }}
port: {{ .port }}
scheme: {{ .scheme }}
httpHeaders:
  - name: Host
    value: {{ .host }}
{{- end -}}

{{/* Replicas: the component's own value, else the environment's default. */}}
{{- define "ewp.replicas" -}}
{{- if hasKey .c "replicas" -}}{{ .c.replicas }}{{- else -}}{{ .root.Values.global.replicas }}{{- end -}}
{{- end -}}