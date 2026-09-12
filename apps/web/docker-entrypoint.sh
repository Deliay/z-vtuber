#!/bin/sh
set -e

API_BACKEND=${API_BACKEND:-backend:8080}
NOTIFY_BACKEND=${NOTIFY_BACKEND:-notifier:8080}
COOKIE_BACKEND=${COOKIE_BACKEND:-cookie:8080}

envsubst '$API_BACKEND $NOTIFY_BACKEND $COOKIE_BACKEND' < /etc/nginx/nginx.conf.template > /etc/nginx/nginx.conf

exec nginx -g "daemon off;"