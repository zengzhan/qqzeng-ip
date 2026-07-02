FROM python:3.12-alpine

WORKDIR /app
COPY . /app

# 生成全国号码区域数据
RUN python3 /app/qqzeng-phone-6.0/python/export_region_numbers.py --start-prefix 130 --end-prefix 199 --out-dir /app/qqzeng-phone-6.0/python/output_cn

EXPOSE 8010

CMD ["python3", "/app/tools/local-data-html/railway_static_server.py"]
