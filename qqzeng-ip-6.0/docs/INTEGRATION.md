# qqzeng-ip 6.0 集成示例

本文档提供qqzeng-ip 6.0在各种流行框架和场景中的集成示例。

## Web框架集成

### Node.js

#### Express.js 集成
```javascript
const express = require('express');
const IpDbSearch = require('./lib/IpDbSearch');

const app = express();
const searcher = IpDbSearch.getInstance();

// 中间件：IP地理位置查询
app.use('/api/location/:ip', (req, res) => {
    try {
        const location = searcher.find(req.params.ip);
        const [continent, country, province, city, district, isp, 
              areaCode, countryEn, isoCode, longitude, latitude] = location.split('|');
        
        res.json({
            ip: req.params.ip,
            location: {
                continent,
                country,
                province,
                city,
                district,
                isp,
                areaCode,
                countryEn,
                isoCode,
                longitude: parseFloat(longitude),
                latitude: parseFloat(latitude)
            }
        });
    } catch (error) {
        res.status(500).json({ error: 'Internal server error' });
    }
});

// 批量查询接口
app.post('/api/location/batch', express.json(), (req, res) => {
    const { ips } = req.body;
    const results = {};
    
    for (const ip of ips) {
        results[ip] = searcher.find(ip);
    }
    
    res.json({ results });
});

app.listen(3000, () => {
    console.log('Server running on port 3000');
});
```

#### Koa.js 集成
```javascript
const Koa = require('koa');
const Router = require('@koa/router');
const IpDbSearch = require('./lib/IpDbSearch');

const app = new Koa();
const router = new Router();
const searcher = IpDbSearch.getInstance();

// IP地理位置查询
router.get('/api/location/:ip', async (ctx) => {
    const location = searcher.find(ctx.params.ip);
    
    if (location) {
        const [continent, country, province, city] = location.split('|');
        ctx.body = {
            ip: ctx.params.ip,
            location: { continent, country, province, city }
        };
    } else {
        ctx.status = 404;
        ctx.body = { error: 'IP not found' };
    }
});

app.use(router.routes());
app.listen(3000);
```

### Java

#### Spring Boot 集成
```java
@RestController
@RequestMapping("/api")
public class LocationController {
    
    private final IpDbSearch searcher = IpDbSearch.getInstance();
    
    @GetMapping("/location/{ip}")
    public ResponseEntity<LocationResponse> getLocation(@PathVariable String ip) {
        String location = searcher.find(ip);
        
        if (location.isEmpty()) {
            return ResponseEntity.notFound().build();
        }
        
        String[] parts = location.split("\\|");
        LocationResponse response = LocationResponse.builder()
            .ip(ip)
            .continent(parts[0])
            .country(parts[1])
            .province(parts[2])
            .city(parts[3])
            .district(parts[4])
            .isp(parts[5])
            .areaCode(parts[6])
            .countryEn(parts[7])
            .isoCode(parts[8])
            .longitude(Double.parseDouble(parts[9]))
            .latitude(Double.parseDouble(parts[10]))
            .build();
            
        return ResponseEntity.ok(response);
    }
    
    @PostMapping("/location/batch")
    public ResponseEntity<Map<String, String>> getBatchLocation(@RequestBody List<String> ips) {
        Map<String, String> results = new HashMap<>();
        
        for (String ip : ips) {
            results.put(ip, searcher.find(ip));
        }
        
        return ResponseEntity.ok(results);
    }
}

@Data
@Builder
class LocationResponse {
    private String ip;
    private String continent;
    private String country;
    private String province;
    private String city;
    private String district;
    private String isp;
    private String areaCode;
    private String countryEn;
    private String isoCode;
    private Double longitude;
    private Double latitude;
}
```

#### Vert.x 集成
```java
public class LocationVerticle extends AbstractVerticle {
    
    private final IpDbSearch searcher = IpDbSearch.getInstance();
    
    @Override
    public void start() {
        Router router = Router.router(vertx);
        
        // 单个IP查询
        router.get("/api/location/:ip").handler(ctx -> {
            String ip = ctx.pathParam("ip");
            String location = searcher.find(ip);
            
            if (location.isEmpty()) {
                ctx.response().setStatusCode(404).end();
            } else {
                ctx.response()
                    .putHeader("Content-Type", "application/json")
                    .end(createJsonResponse(ip, location));
            }
        });
        
        // 批量查询
        router.post("/api/location/batch").handler(ctx -> {
            ctx.request().bodyHandler(body -> {
                JsonObject json = new JsonObject(body.toString());
                JsonArray ips = json.getJsonArray("ips");
                JsonObject results = new JsonObject();
                
                for (int i = 0; i < ips.size(); i++) {
                    String ip = ips.getString(i);
                    results.put(ip, searcher.find(ip));
                }
                
                ctx.response()
                    .putHeader("Content-Type", "application/json")
                    .end(results.encode());
            });
        });
        
        vertx.createHttpServer()
            .requestHandler(router)
            .listen(8080);
    }
    
    private String createJsonResponse(String ip, String location) {
        String[] parts = location.split("\\|");
        JsonObject response = new JsonObject();
        response.put("ip", ip);
        response.put("continent", parts[0]);
        response.put("country", parts[1]);
        response.put("province", parts[2]);
        response.put("city", parts[3]);
        return response.encode();
    }
}
```

### Go

#### Gin 集成
```go
package main

import (
    "github.com/gin-gonic/gin"
    "qqzeng-ip/ipdb"
    "net/http"
    "strings"
)

func main() {
    searcher, _ := ipdb.Instance()
    r := gin.Default()
    
    // 单个IP查询
    r.GET("/api/location/:ip", func(c *gin.Context) {
        ip := c.Param("ip")
        location := searcher.Find(ip)
        
        if location == "" {
            c.JSON(http.StatusNotFound, gin.H{"error": "IP not found"})
            return
        }
        
        parts := strings.Split(location, "|")
        c.JSON(http.StatusOK, gin.H{
            "ip": ip,
            "location": gin.H{
                "continent": parts[0],
                "country":   parts[1],
                "province":  parts[2],
                "city":      parts[3],
                "district":  parts[4],
                "isp":       parts[5],
                "areaCode":  parts[6],
                "countryEn": parts[7],
                "isoCode":   parts[8],
                "longitude": parts[9],
                "latitude":  parts[10],
            },
        })
    })
    
    // 批量查询
    r.POST("/api/location/batch", func(c *gin.Context) {
        var request struct {
            IPs []string `json:"ips"`
        }
        
        if err := c.ShouldBindJSON(&request); err != nil {
            c.JSON(http.StatusBadRequest, gin.H{"error": err.Error()})
            return
        }
        
        results := make(map[string]string)
        for _, ip := range request.IPs {
            results[ip] = searcher.Find(ip)
        }
        
        c.JSON(http.StatusOK, gin.H{"results": results})
    })
    
    r.Run(":8080")
}
```

#### Echo 集成
```go
package main

import (
    "github.com/labstack/echo/v4"
    "github.com/labstack/echo/v4/middleware"
    "qqzeng-ip/ipdb"
    "net/http"
    "strings"
)

func main() {
    e := echo.New()
    
    // 中间件
    e.Use(middleware.Logger())
    e.Use(middleware.Recover())
    
    searcher, _ := ipdb.Instance()
    
    // 单个IP查询
    e.GET("/api/location/:ip", func(c echo.Context) error {
        ip := c.Param("ip")
        location := searcher.Find(ip)
        
        if location == "" {
            return c.JSON(http.StatusNotFound, map[string]string{"error": "IP not found"})
        }
        
        parts := strings.Split(location, "|")
        return c.JSON(http.StatusOK, map[string]interface{}{
            "ip": ip,
            "location": map[string]string{
                "continent": parts[0],
                "country":   parts[1],
                "province":  parts[2],
                "city":      parts[3],
                "district":  parts[4],
                "isp":       parts[5],
                "areaCode":  parts[6],
                "countryEn": parts[7],
                "isoCode":   parts[8],
                "longitude": parts[9],
                "latitude":  parts[10],
            },
        })
    })
    
    e.Logger.Fatal(e.Start(":1323"))
}
```

### .NET

#### ASP.NET Core 集成
```csharp
using Microsoft.AspNetCore.Mvc;
using qqzengIp;

[ApiController]
[Route("api")]
public class LocationController : ControllerBase
{
    private readonly IpDbSearch _searcher = IpDbSearch.Instance;
    
    [HttpGet("location/{ip}")]
    public ActionResult<LocationResponse> GetLocation(string ip)
    {
        var location = _searcher.Find(ip);
        
        if (string.IsNullOrEmpty(location))
        {
            return NotFound();
        }
        
        var parts = location.Split('|');
        return Ok(new LocationResponse
        {
            Ip = ip,
            Continent = parts[0],
            Country = parts[1],
            Province = parts[2],
            City = parts[3],
            District = parts[4],
            Isp = parts[5],
            AreaCode = parts[6],
            CountryEn = parts[7],
            IsoCode = parts[8],
            Longitude = double.Parse(parts[9]),
            Latitude = double.Parse(parts[10])
        });
    }
    
    [HttpPost("location/batch")]
    public ActionResult<Dictionary<string, string>> GetBatchLocation([FromBody] BatchRequest request)
    {
        var results = new Dictionary<string, string>();
        
        foreach (var ip in request.Ips)
        {
            results[ip] = _searcher.Find(ip);
        }
        
        return Ok(results);
    }
}

public class LocationResponse
{
    public string Ip { get; set; }
    public string Continent { get; set; }
    public string Country { get; set; }
    public string Province { get; set; }
    public string City { get; set; }
    public string District { get; set; }
    public string Isp { get; set; }
    public string AreaCode { get; set; }
    public string CountryEn { get; set; }
    public string IsoCode { get; set; }
    public double Longitude { get; set; }
    public double Latitude { get; set; }
}

public class BatchRequest
{
    public List<string> Ips { get; set; }
}
```

### Python

#### Flask 集成
```python
from flask import Flask, jsonify, request
from qqzeng_ip.ipdb import IpDbSearch

app = Flask(__name__)
searcher = IpDbSearch()

@app.route('/api/location/<ip>')
def get_location(ip):
    location = searcher.find(ip)
    
    if not location:
        return jsonify({'error': 'IP not found'}), 404
    
    parts = location.split('|')
    return jsonify({
        'ip': ip,
        'location': {
            'continent': parts[0],
            'country': parts[1],
            'province': parts[2],
            'city': parts[3],
            'district': parts[4],
            'isp': parts[5],
            'area_code': parts[6],
            'country_en': parts[7],
            'iso_code': parts[8],
            'longitude': float(parts[9]),
            'latitude': float(parts[10])
        }
    })

@app.route('/api/location/batch', methods=['POST'])
def get_batch_location():
    data = request.get_json()
    ips = data.get('ips', [])
    
    results = {}
    for ip in ips:
        results[ip] = searcher.find(ip)
    
    return jsonify({'results': results})

if __name__ == '__main__':
    app.run(debug=True, host='0.0.0.0', port=5000)
```

#### FastAPI 集成
```python
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from typing import List, Dict
from qqzeng_ip.ipdb import IpDbSearch

app = FastAPI()
searcher = IpDbSearch()

class LocationResponse(BaseModel):
    ip: str
    continent: str
    country: str
    province: str
    city: str
    district: str
    isp: str
    area_code: str
    country_en: str
    iso_code: str
    longitude: float
    latitude: float

class BatchRequest(BaseModel):
    ips: List[str]

@app.get("/api/location/{ip}", response_model=LocationResponse)
async def get_location(ip: str):
    location = searcher.find(ip)
    
    if not location:
        raise HTTPException(status_code=404, detail="IP not found")
    
    parts = location.split('|')
    return LocationResponse(
        ip=ip,
        continent=parts[0],
        country=parts[1],
        province=parts[2],
        city=parts[3],
        district=parts[4],
        isp=parts[5],
        area_code=parts[6],
        country_en=parts[7],
        iso_code=parts[8],
        longitude=float(parts[9]),
        latitude=float(parts[10])
    )

@app.post("/api/location/batch")
async def get_batch_location(request: BatchRequest):
    results = {}
    for ip in request.ips:
        results[ip] = searcher.find(ip)
    return {"results": results}
```

#### Django 集成
```python
# views.py
from django.http import JsonResponse
from django.views.decorators.http import require_http_methods
from django.views.decorators.csrf import csrf_exempt
import json
from qqzeng_ip.ipdb import IpDbSearch

searcher = IpDbSearch()

@csrf_exempt
def get_location(request, ip):
    location = searcher.find(ip)
    
    if not location:
        return JsonResponse({'error': 'IP not found'}, status=404)
    
    parts = location.split('|')
    return JsonResponse({
        'ip': ip,
        'location': {
            'continent': parts[0],
            'country': parts[1],
            'province': parts[2],
            'city': parts[3],
            'district': parts[4],
            'isp': parts[5],
            'area_code': parts[6],
            'country_en': parts[7],
            'iso_code': parts[8],
            'longitude': float(parts[9]),
            'latitude': float(parts[10])
        }
    })

@csrf_exempt
@require_http_methods(["POST"])
def get_batch_location(request):
    data = json.loads(request.body)
    ips = data.get('ips', [])
    
    results = {}
    for ip in ips:
        results[ip] = searcher.find(ip)
    
    return JsonResponse({'results': results})

# urls.py
from django.urls import path
from . import views

urlpatterns = [
    path('api/location/<str:ip>/', views.get_location, name='get_location'),
    path('api/location/batch/', views.get_batch_location, name='get_batch_location'),
]
```

### PHP

#### Laravel 集成
```php
<?php

namespace App\Http\Controllers;

use Illuminate\Http\Request;
use Qqzeng\Ip\IpDbSearch;

class LocationController extends Controller
{
    private $searcher;
    
    public function __construct()
    {
        $this->searcher = IpDbSearch::getInstance();
    }
    
    public function getLocation($ip)
    {
        $location = $this->searcher->find($ip);
        
        if (empty($location)) {
            return response()->json(['error' => 'IP not found'], 404);
        }
        
        $parts = explode('|', $location);
        
        return response()->json([
            'ip' => $ip,
            'location' => [
                'continent' => $parts[0],
                'country' => $parts[1],
                'province' => $parts[2],
                'city' => $parts[3],
                'district' => $parts[4],
                'isp' => $parts[5],
                'area_code' => $parts[6],
                'country_en' => $parts[7],
                'iso_code' => $parts[8],
                'longitude' => (float)$parts[9],
                'latitude' => (float)$parts[10],
            ]
        ]);
    }
    
    public function getBatchLocation(Request $request)
    {
        $ips = $request->input('ips', []);
        $results = [];
        
        foreach ($ips as $ip) {
            $results[$ip] = $this->searcher->find($ip);
        }
        
        return response()->json(['results' => $results]);
    }
}
```

#### ThinkPHP 集成
```php
<?php

namespace app\controller;

use app\BaseController;
use Qqzeng\Ip\IpDbSearch;

class Location extends BaseController
{
    private $searcher;
    
    public function __construct()
    {
        $this->searcher = IpDbSearch::getInstance();
    }
    
    public function getLocation($ip)
    {
        $location = $this->searcher->find($ip);
        
        if (empty($location)) {
            return json(['code' => 404, 'msg' => 'IP not found']);
        }
        
        $parts = explode('|', $location);
        
        return json([
            'code' => 200,
            'data' => [
                'ip' => $ip,
                'location' => [
                    'continent' => $parts[0],
                    'country' => $parts[1],
                    'province' => $parts[2],
                    'city' => $parts[3],
                    'district' => $parts[4],
                    'isp' => $parts[5],
                    'area_code' => $parts[6],
                    'country_en' => $parts[7],
                    'iso_code' => $parts[8],
                    'longitude' => (float)$parts[9],
                    'latitude' => (float)$parts[10],
                ]
            ]
        ]);
    }
}
```

## 微服务集成

### Docker 部署

#### Node.js Dockerfile
```dockerfile
FROM node:25-alpine

WORKDIR /app
COPY package*.json ./
RUN npm ci --only=production

COPY . .
COPY ../data/qqzeng-ip-6.0-global.db ./

EXPOSE 3000
CMD ["node", "server.js"]
```

#### Java Dockerfile
```dockerfile
FROM openjdk:21-jdk-slim

WORKDIR /app
COPY target/location-service.jar ./
COPY ../data/qqzeng-ip-6.0-global.db ./

EXPOSE 8080
CMD ["java", "-jar", "location-service.jar"]
```

#### Go Dockerfile
```dockerfile
FROM golang:1.24-alpine AS builder

WORKDIR /app
COPY go.mod go.sum ./
RUN go mod download

COPY . .
COPY ../data/qqzeng-ip-6.0-global.db ./
RUN go build -o location-service

FROM alpine:latest
RUN apk --no-cache add ca-certificates
WORKDIR /root/

COPY --from=builder /app/location-service .
COPY --from=builder /app/qqzeng-ip-6.0-global.db .

EXPOSE 8080
CMD ["./location-service"]
```

### Kubernetes 部署

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: location-service
spec:
  replicas: 3
  selector:
    matchLabels:
      app: location-service
  template:
    metadata:
      labels:
        app: location-service
    spec:
      containers:
      - name: location-service
        image: location-service:latest
        ports:
        - containerPort: 8080
        resources:
          requests:
            memory: "64Mi"
            cpu: "50m"
          limits:
            memory: "128Mi"
            cpu: "100m"
---
apiVersion: v1
kind: Service
metadata:
  name: location-service
spec:
  selector:
    app: location-service
  ports:
  - port: 80
    targetPort: 8080
  type: LoadBalancer
```

## 数据库集成

### Redis 缓存层
```python
import redis
from qqzeng_ip.ipdb import IpDbSearch

class LocationService:
    def __init__(self):
        self.searcher = IpDbSearch()
        self.redis = redis.Redis(host='localhost', port=6379, db=0)
        self.cache_ttl = 3600  # 1小时
    
    def get_location(self, ip):
        # 先检查缓存
        cached = self.redis.get(f"location:{ip}")
        if cached:
            return cached.decode('utf-8')
        
        # 查询数据库
        location = self.searcher.find(ip)
        
        # 存入缓存
        if location:
            self.redis.setex(f"location:{ip}", self.cache_ttl, location)
        
        return location
```

### 消息队列集成
```java
// RabbitMQ 集成示例
@Component
public class LocationProcessor {
    
    private final IpDbSearch searcher = IpDbSearch.getInstance();
    
    @RabbitListener(queues = "location.queue")
    public void processLocation(String ip) {
        String location = searcher.find(ip);
        
        // 处理结果
        LocationResult result = new LocationResult(ip, location);
        
        // 发送到结果队列
        rabbitTemplate.convertAndSend("location.result.queue", result);
    }
}
```

## 监控和日志

### Prometheus 指标
```go
package main

import (
    "github.com/prometheus/client_golang/prometheus"
    "github.com/prometheus/client_golang/prometheus/promhttp"
    "net/http"
)

var (
    locationQueries = prometheus.NewCounterVec(
        prometheus.CounterOpts{
            Name: "location_queries_total",
            Help: "Total number of location queries",
        },
        []string{"status"},
    )
    
    locationQueryDuration = prometheus.NewHistogram(
        prometheus.HistogramOpts{
            Name: "location_query_duration_seconds",
            Help: "Duration of location queries",
        },
    )
)

func init() {
    prometheus.MustRegister(locationQueries)
    prometheus.MustRegister(locationQueryDuration)
}

func main() {
    http.Handle("/metrics", promhttp.Handler())
    http.ListenAndServe(":9090", nil)
}
```

### 结构化日志
```python
import logging
import json
from qqzeng_ip.ipdb import IpDbSearch

# 配置结构化日志
logging.basicConfig(
    level=logging.INFO,
    format='{"timestamp": "%(asctime)s", "level": "%(levelname)s", "message": "%(message)s"}'
)
logger = logging.getLogger(__name__)

class LocationService:
    def __init__(self):
        self.searcher = IpDbSearch()
    
    def get_location(self, ip):
        start_time = time.time()
        
        try:
            location = self.searcher.find(ip)
            duration = time.time() - start_time
            
            logger.info(json.dumps({
                "event": "location_query",
                "ip": ip,
                "duration": duration,
                "success": bool(location)
            }))
            
            return location
            
        except Exception as e:
            logger.error(json.dumps({
                "event": "location_query_error",
                "ip": ip,
                "error": str(e)
            }))
            raise
```

---

**文档版本**: 6.0  
**最后更新**: 2026-01-06  
**维护者**: qqzeng-ip团队