# production only

FROM node:24-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
# Angular resolves the API origin at runtime from assets/app-config.json, so
# the same build arg name keeps working: a value bakes the API origin into the
# served config, an empty value means same-origin.
ARG VITE_API_URL
ENV VITE_API_URL=$VITE_API_URL
RUN npm run build

FROM nginx:alpine
# Copy built static files to Nginx (the application builder emits dist/browser)
COPY --from=build /app/dist/browser /usr/share/nginx/html
# SPA routing fix (fallback to index.html)
RUN sed -i 's/try_files $uri $uri\/ =404;/try_files $uri $uri\/ \/index.html;/g' /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
