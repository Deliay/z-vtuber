#!/bin/sh
set -e

API_BACKEND=${API_BACKEND:-backend:8080}

envsubst '$API_BACKEND' < /etc/nginx/nginx.conf.template > /etc/nginx/conf.d/default.conf

exec nginx -g "daemon off;"