FROM node:26-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
ARG VITE_API_BASE_URL=.
ARG VITE_OIDC_AUTHORITY=
ARG VITE_OIDC_ISSUER=
ARG VITE_OIDC_CLIENT_ID=aegisscan-web
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
ENV VITE_OIDC_AUTHORITY=$VITE_OIDC_AUTHORITY
ENV VITE_OIDC_ISSUER=$VITE_OIDC_ISSUER
ENV VITE_OIDC_CLIENT_ID=$VITE_OIDC_CLIENT_ID
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine
ENV AEGIS_API_UPSTREAM=aegisscan:8080
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --chmod=755 docker-entrypoint.d/40-aegis-config.sh /docker-entrypoint.d/40-aegis-config.sh
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
