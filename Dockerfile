FROM python:3.12-alpine

WORKDIR /app
COPY . /app

ENV PORT=8010
EXPOSE 8010

CMD ["sh", "-c", "python3 -m http.server ${PORT} --bind 0.0.0.0 --directory /app"]
