FROM python:3.12-alpine

WORKDIR /app
COPY . /app

EXPOSE 8010

CMD ["python3", "-m", "http.server", "8010", "--bind", "0.0.0.0", "--directory", "/app"]
