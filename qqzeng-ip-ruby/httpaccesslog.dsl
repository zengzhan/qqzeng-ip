input {
    kafka {
        topics => [ "httpaccess_log" ]
        codec => json
    }
}
filter {
    zengip {
        source => "request[ip]"
        target => "request"
        database => "../lib/zengip.dat"

        add_field => [ "request[location]", "%{request[latitude]},%{request[longitude]}" ]
    }

    mutate {
        remove_field => [ "@version" ]
        convert => [ "[geoip][coordinates]", "float" ]
    }
}
output {
  stdout { codec => rubydebug }
  elasticsearch {
          hosts => ["127.0.0.1:9200"]
          index => "httpaccess-log-%{+YYYY.MM}"
          document_type => "http"
          document_id => "%{req_id}"
          flush_size => 20000
          idle_flush_time => 10
          sniffing => false
          template_overwrite => true
      }
}
