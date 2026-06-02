FROM python:3.12-alpine

WORKDIR /app
COPY . /app

EXPOSE 8010

CMD ["python3", "/app/tools/local-data-html/railway_static_server.py"]
