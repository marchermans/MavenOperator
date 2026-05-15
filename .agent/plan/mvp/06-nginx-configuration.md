# 06 — NGINX Configuration
## Base Image
`nginx:1.27-alpine` — small, well-maintained, includes WebDAV module.
Custom build is NOT needed for MVP because `nginx:alpine` includes `ngx_http_dav_module` in the standard build.
---
## Hosted Repository Template
```nginx
server {
    listen 80;
    # Artifact root
    root /var/maven/repository;
    autoindex on;
    location /repository/{{.Name}}/ {
        alias /var/maven/repository/;
        # Upload authentication (PUT/DELETE require credentials)
        limit_except GET HEAD OPTIONS {
            {{- if eq .Auth.Upload.Policy "Authenticated"}}
            auth_basic "Maven Upload - {{.Name}}";
            auth_basic_user_file /etc/nginx/auth/.htpasswd;
            {{- else}}
            allow all;
            {{- end}}
        }
        # Download authentication
        {{- if eq .Auth.Download.Policy "Authenticated"}}
        auth_basic "Maven - {{.Name}}";
        auth_basic_user_file /etc/nginx/auth/.htpasswd;
        {{- end}}
        # WebDAV support for Maven deploys
        dav_methods PUT DELETE MKCOL COPY MOVE;
        dav_ext_methods PROPFIND OPTIONS;
        create_full_put_path on;
        dav_access user:rw group:r all:r;
        client_max_body_size 512m;
    }
}
```
---
## Proxy Repository Template
```nginx
proxy_cache_path /var/cache/nginx/{{.Name}}
    levels=1:2
    keys_zone={{.Name}}_cache:10m
    max_size=10g
    inactive=1d
    use_temp_path=off;
server {
    listen 80;
    location /repository/{{.Name}}/ {
        {{- if eq .Auth.Download.Policy "Authenticated"}}
        auth_basic "Maven Proxy - {{.Name}}";
        auth_basic_user_file /etc/nginx/auth/.htpasswd;
        {{- end}}
        proxy_pass {{.Upstream.URL}}/;
        proxy_cache {{.Name}}_cache;
        proxy_cache_valid 200 {{.Upstream.CacheTtl}};
        proxy_cache_valid 404 1m;
        {{- if .Upstream.Auth}}
        proxy_set_header Authorization "Basic {{.Upstream.Auth.Base64}}";
        {{- end}}
        proxy_set_header Host $proxy_host;
    }
}
```
---
## Virtual Repository Template
The Virtual repo does NOT use NGINX for artifact routing — that is handled by the C# proxy. NGINX acts as a TLS termination + reverse proxy to the C# pod.
```nginx
server {
    listen 80;
    location /repository/{{.Name}}/ {
        {{- if eq .Auth.Download.Policy "Authenticated"}}
        auth_basic "Maven Group - {{.Name}}";
        auth_basic_user_file /etc/nginx/auth/.htpasswd;
        {{- end}}
        proxy_pass http://{{.Name}}-proxy-svc:8080/;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```
---
## ConfigMap Management
The operator renders these templates using `Scriban` or `Handlebars.Net` (or simple string interpolation for MVP) and stores the result in a ConfigMap. The NGINX Deployment mounts the ConfigMap as a volume.
When the ConfigMap changes (detected via resource version), the operator triggers a rolling restart of the Deployment via a `kubectl rollout restart` equivalent (patching the pod template annotation `maven.operator.io/config-hash`).
