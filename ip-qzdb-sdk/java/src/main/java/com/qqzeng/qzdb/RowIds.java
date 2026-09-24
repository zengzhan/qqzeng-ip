package com.qqzeng.qzdb;

/**
 * 对应 IP 行中引用的 ID 结构体 (用于底层 lookupIds)
 *
 * @param geoId   地理位置 ID
 * @param asnId   ASN ID
 * @param usageId 应用场景 ID
 */
public record RowIds(int geoId, int asnId, int usageId) {
    /** @return the geographic ID as an unsigned 32-bit value */
    public long geoIdUnsigned() {
        return Integer.toUnsignedLong(geoId);
    }

    /** @return the ASN ID as an unsigned 32-bit value */
    public long asnIdUnsigned() {
        return Integer.toUnsignedLong(asnId);
    }

    /** @return the usage ID as an unsigned 32-bit value */
    public long usageIdUnsigned() {
        return Integer.toUnsignedLong(usageId);
    }
}
