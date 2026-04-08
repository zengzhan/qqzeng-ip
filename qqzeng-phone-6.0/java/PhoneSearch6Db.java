import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

public final class PhoneSearch6Db {
    private static final int HeaderSize = 32;
    private static final int PrefixCount = 200;
    private static final int BitmapPopCountOffset = 0x4E2;

    private byte[] data = new byte[0];
    private String[] regionIsps = new String[0];
    private final IndexEntry[] index = new IndexEntry[PrefixCount];

    private static class Holder {
        static final PhoneSearch6Db INSTANCE = new PhoneSearch6Db();
    }

    public static PhoneSearch6Db getInstance() {
        return Holder.INSTANCE;
    }

    private PhoneSearch6Db() {
        loadDatabase();
    }

    private void loadDatabase() {
        String filePath = Paths.get(System.getProperty("user.dir"), "qqzeng-phone-6.0.db").toString();
        try {
            data = Files.readAllBytes(Path.of(filePath));
            ByteBuffer buffer = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN);

            int[] header = new int[8];
            for (int i = 0; i < header.length; i++) {
                header[i] = buffer.getInt(i * 4);
            }

            int regionsStart = HeaderSize;
            int ispsStart = regionsStart + header[1];
            int indexStart = ispsStart + header[2];

            String regionsStr = new String(data, regionsStart, header[1], "UTF-8");
            String[] regions = regionsStr.split("&");
            String ispsStr = new String(data, ispsStart, header[2], "UTF-8");
            String[] isps = ispsStr.split("&");

            int entryOffset = header[3];
            regionIsps = new String[header[4]];
            for (int i = 0; i < regionIsps.length; i++) {
                int entry = (buffer.getShort(entryOffset + i * 2) & 0xFFFF);
                regionIsps[i] = regions[entry >> 5] + "|" + isps[entry & 0x1F];
            }

            int pos = indexStart;
            for (int i = 0; i < PrefixCount; i++) {
                if (pos + 12 > data.length) {
                    index[i] = new IndexEntry(0, 0);
                    pos += 12;
                    continue;
                }
                int prefix = buffer.getInt(pos);
                if (prefix == i) {
                    index[i] = new IndexEntry(
                            buffer.getInt(pos + 4),
                            buffer.getInt(pos + 8)
                    );
                    pos += 12;
                } else {
                    index[i] = new IndexEntry(0, 0);
                }
               
            }
        } catch (IOException e) {
            throw new RuntimeException("Database file not found", e);
        } catch (Exception e) {
            throw new RuntimeException("Invalid database format", e);
        }
    }

    public String query(String phone) {
        if (phone == null || phone.length() != 7 || !phone.matches("\\d{7}")) {
            throw new IllegalArgumentException("Invalid phone number");
        }

        int prefix = parsePhoneSegment(phone.substring(0, 3));
        int subNum = parsePhoneSegment(phone.substring(3, 7));

        if (prefix < 0 || prefix >= PrefixCount) {
            return null;
        }

        IndexEntry entry = index[prefix];
        if (entry.bitmapOffset == 0 || entry.dataOffset == 0) {
            return null;
        }

        int byteIndex = subNum >>> 3; // Equivalent to subNum / 8
        int bitIndex = subNum & 0x07; // Equivalent to subNum % 8

        if (entry.bitmapOffset + byteIndex >= data.length) {
            return null;
        }

        int bitmapValue = data[entry.bitmapOffset + byteIndex] & 0xFF;
        if ((bitmapValue & (1 << bitIndex)) == 0) {
            return null;
        }

        int popCountOffset = entry.bitmapOffset + BitmapPopCountOffset + (byteIndex << 1);
        if (popCountOffset + 2 > data.length) {
            return null;
        }
        int preCount = ByteBuffer.wrap(data, popCountOffset, 2)
                .order(ByteOrder.LITTLE_ENDIAN).getShort() & 0xFFFF;

        int localCount = Integer.bitCount(bitmapValue & ((1 << bitIndex) - 1));

        int dataPos = entry.dataOffset + (preCount + localCount) * 2;
        if (dataPos + 2 > data.length) {
            return null;
        }

        int entryIndex = ByteBuffer.wrap(data, dataPos, 2)
                .order(ByteOrder.LITTLE_ENDIAN).getShort() & 0xFFFF;

        return (entryIndex < regionIsps.length) ? regionIsps[entryIndex] : null;
    }

    private static int parsePhoneSegment(String segment) {
        int result = 0;
        for (char c : segment.toCharArray()) {
            if (c < '0' || c > '9') {
                throw new IllegalArgumentException("Invalid phone segment: " + segment);
            }
            result = result * 10 + (c - '0');
        }
        return result;
    }

    private static class IndexEntry {
        final int bitmapOffset;
        final int dataOffset;

        IndexEntry(int bitmapOffset, int dataOffset) {
            this.bitmapOffset = bitmapOffset;
            this.dataOffset = dataOffset;
        }
    }
}





