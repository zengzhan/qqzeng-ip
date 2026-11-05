//! 面向 AI Agent (MCP / Function Calling) 的 OpenAPI 3.1 与 JSON Schema 元数据导出模块。
//!
//! 无需引入重型序列化依赖（保持 `qzdb` 核心库零 `serde` 强依赖），直接根据当前加载快照的
//! 权威元数据（`edition`、`data_month`、`build_time`、`field_names`）生成符合
//! JSON Schema Draft 2020-12 与 Model Context Protocol (MCP) 的工具清单及高速响应。

use crate::{escape_json, QzdbReader};

impl QzdbReader {
    /// 导出符合 MCP (Model Context Protocol) 与 JSON Schema (Draft 2020-12) 的离线工具清单。
    pub fn export_mcp_tool_manifest(&self) -> String {
        let snap = self.inner();
        let fields_enum = snap
            .field_names
            .iter()
            .map(|f| format!("\"{}\"", escape_json(f)))
            .collect::<Vec<_>>()
            .join(",");

        let edition = escape_json(&snap.edition);
        let version = escape_json(&snap.version);
        let data_month = escape_json(&snap.data_month);
        let build_time = escape_json(&snap.build_time);
        let file_hash = self.get_file_hash();

        format!(
            concat!(
                "{{",
                "\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",",
                "\"mcp_version\":\"2025-06-18\",",
                "\"database_meta\":{{",
                "\"edition\":\"{edition}\",",
                "\"version\":\"{version}\",",
                "\"data_month\":\"{data_month}\",",
                "\"build_time\":\"{build_time}\",",
                "\"crc32_hash\":\"{file_hash}\",",
                "\"row_count\":{row_count},",
                "\"has_ipv4\":{has_v4},",
                "\"has_ipv6\":{has_v6},",
                "\"available_fields\":[{fields_enum}]",
                "}},",
                "\"tools\":[",
                "{{",
                "\"name\":\"qzdb_inspect_ip_fast\",",
                "\"description\":\"Zero-allocation bitmask inspection for IPv4/IPv6 addresses (is_datacenter, is_domestic, is_crawler, is_ai_crawler, is_mobile, is_vpn, cidr, row_id).\",",
                "\"inputSchema\":{{",
                "\"type\":\"object\",",
                "\"properties\":{{",
                "\"ips\":{{\"type\":\"array\",\"items\":{{\"type\":\"string\"}},\"maxItems\":1000,\"description\":\"IPv4 or IPv6 addresses to inspect.\"}}",
                "}},",
                "\"required\":[\"ips\"]",
                "}}",
                "}},",
                "{{",
                "\"name\":\"qzdb_lookup_geo_fields\",",
                "\"description\":\"Offline high-precision geolocation and ASN lookup from local QZDB ({edition} build {build_time}).\",",
                "\"inputSchema\":{{",
                "\"type\":\"object\",",
                "\"properties\":{{",
                "\"ips\":{{\"type\":\"array\",\"items\":{{\"type\":\"string\"}},\"maxItems\":500}},",
                "\"fields\":{{\"type\":\"array\",\"items\":{{\"type\":\"string\",\"enum\":[{fields_enum}]}},\"description\":\"Optional field projection to minimize token usage.\"}}",
                "}},",
                "\"required\":[\"ips\"]",
                "}}",
                "}}",
                "]",
                "}}"
            ),
            edition = edition,
            version = version,
            data_month = data_month,
            build_time = build_time,
            file_hash = file_hash,
            row_count = snap.row_count,
            has_v4 = snap.has_v4,
            has_v6 = snap.has_v6,
            fields_enum = fields_enum,
        )
    }

    /// 针对 AI Agent 工具调用的批量快速位掩码嗅探 JSON 输出（免除全量字段序列化开销）。
    pub fn agent_inspect_batch_json(&self, ips: &[&str]) -> String {
        let snap = self.snap.load();
        let mut out = String::with_capacity(ips.len() * 160 + 32);
        out.push_str("{\"results\":[");
        for (i, &ip) in ips.iter().enumerate() {
            if i > 0 {
                out.push(',');
            }
            let row_id = snap.lookup_row_id(ip);
            let flags = snap.inspect_flags_by_row(row_id);
            let cidr = snap.lookup_cidr(ip).unwrap_or_default();
            let usage = flags.known_usage().map(|u| u.raw()).unwrap_or("");
            out.push_str(&format!(
                concat!(
                    "{{",
                    "\"ip\":\"{}\",",
                    "\"hit\":{},",
                    "\"row_id\":{},",
                    "\"cidr\":\"{}\",",
                    "\"flags\":{},",
                    "\"is_domestic\":{},",
                    "\"is_greater_china\":{},",
                    "\"is_datacenter\":{},",
                    "\"is_crawler\":{},",
                    "\"is_ai_crawler\":{},",
                    "\"is_mobile\":{},",
                    "\"is_vpn\":{},",
                    "\"is_private_or_reserved\":{},",
                    "\"known_usage\":\"{}\"",
                    "}}"
                ),
                escape_json(ip),
                flags.is_hit(),
                row_id,
                escape_json(&cidr),
                flags.bits(),
                flags.is_domestic(),
                flags.is_greater_china(),
                flags.is_datacenter(),
                flags.is_crawler(),
                flags.is_ai_crawler(),
                flags.is_mobile(),
                flags.is_vpn(),
                flags.is_private_or_reserved(),
                usage
            ));
        }
        out.push_str("]}");
        out
    }
}
