package abc.com.ip;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.UnsupportedEncodingException;
import java.nio.file.Files;
import java.nio.file.Path;

public class IPSearch {
	private static final int INDEX_RECORD_LENGTH = 7;
	private static final byte REDIRECT_MODE_1 = 0x01;
	private static final byte REDIRECT_MODE_2 = 0x02;
	private static final byte STRING_END = '\0';

	private final byte[] data;
	private final long indexHead;
	private final long indexTail;
	private final byte[] stringBuf = new byte[64];

	
	public IPSearch() throws IOException {
		final InputStream in = IPSearch.class.getClassLoader().getResourceAsStream("qqzeng-ip.dat");
		final ByteArrayOutputStream out = new ByteArrayOutputStream(10 * 1024 * 1024); // 10MB
		final byte[] buffer = new byte[4096];
		while (true) {
			final int r = in.read(buffer);
			if (r == -1) {
				break;
			}
			out.write(buffer, 0, r);
		}
		data = out.toByteArray();
		indexHead = readLong32(0);
		indexTail = readLong32(4);
	}

	
	public IPSearch(final byte[] data) {
		this.data = data;
		indexHead = readLong32(0);
		indexTail = readLong32(4);
	}

	
	public IPSearch(final Path file) throws IOException {
		data = Files.readAllBytes(file);
		indexHead = readLong32(0);
		indexTail = readLong32(4);
	}

		
	public IPEntity Query(final String ip) {
		final long ipNum = toNumericIP(ip);
		final IPIndex idx = searchIndex(ipNum);
		if (idx == null) {
			return new IPEntity(ip);
		}
		return readIP(ip, idx);
	}

	private long getMiddleOffset(final long begin, final long end) {
		long records = (end - begin) / INDEX_RECORD_LENGTH;
		records >>= 1;
		if (records == 0) {
			records = 1;
		}
		return begin + (records * INDEX_RECORD_LENGTH);
	}

	private IPIndex readIndex(final int offset) {
		final long min = readLong32(offset);
		final int record = readInt24(offset + 4);
		final long max = readLong32(record);
		return new IPIndex(min, max, record);
	}

	private int readInt24(final int offset) {
		int v = data[offset] & 0xFF;
		v |= ((data[offset + 1] << 8) & 0xFF00);
		v |= ((data[offset + 2] << 16) & 0xFF0000);
		return v;
	}

	private IPEntity readIP(final String ip, final IPIndex idx) {
		final int pos = idx.recordOffset + 4;
		final byte mode = data[pos];
		final IPEntity z = new IPEntity(ip);
		if (mode == REDIRECT_MODE_1) {
			final int offset = readInt24(pos + 1);
			if (data[offset] == REDIRECT_MODE_2) {
				readMode2(z, offset);
			} else {
				final IPString country = readString(offset);
				final String subInfo = readSubInfo(offset + country.length);
				z.setCountry(country.string);
				z.setSubInfo(subInfo);
			}
		} else if (mode == REDIRECT_MODE_2) {
			readMode2(z, pos);
		} else {
			final IPString country = readString(pos);
			final String subInfo = readSubInfo(pos + country.length);
			z.setCountry(country.string);
			z.setSubInfo(subInfo);
		}
		return z;
	}

	private long readLong32(final int offset) {
		long v = data[offset] & 0xFFL;
		v |= (data[offset + 1] << 8L) & 0xFF00L;
		v |= ((data[offset + 2] << 16L) & 0xFF0000L);
		v |= ((data[offset + 3] << 24L) & 0xFF000000L);
		return v;
	}

	private void readMode2(final IPEntity z, final int offset) {
		final int countryOffset = readInt24(offset + 1);
		final String main = readString(countryOffset).string;
		final String sub = readSubInfo(offset + 4);
		z.setCountry(main);
		z.setSubInfo(sub);
	}

	private IPString readString(final int offset) {
		int i = 0;
		for (;; i++) {
			final byte b = data[offset + i];
			if (STRING_END == b) {
				break;
			}
			stringBuf[i] = b;
		}
		try {
			return new IPString(new String(stringBuf, 0, i, "GB18030"), i + 1);
		} catch (final UnsupportedEncodingException e) {
			return new IPString("", 0);
		}
	}

	private String readSubInfo(final int offset) {
		final byte b = data[offset];
		if ((b == REDIRECT_MODE_1) || (b == REDIRECT_MODE_2)) {
			final int areaOffset = readInt24(offset + 1);
			if (areaOffset == 0) {
				return "";
			} else {
				return readString(areaOffset).string;
			}
		} else {
			return readString(offset).string;
		}
	}

	private IPIndex searchIndex(final long ip) {
		long head = indexHead;
		long tail = indexTail;
		while (tail > head) {
			final long cur = getMiddleOffset(head, tail);
			final IPIndex idx = readIndex((int) cur);
			if ((ip >= idx.minIP) && (ip <= idx.maxIP)) {
				return idx;
			}
			if ((cur == head) || (cur == tail)) {
				return idx;
			}
			if (ip < idx.minIP) {
				tail = cur;
			} else if (ip > idx.maxIP) {
				head = cur;
			} else {
				return idx;
			}
		}
		return null;
	}

	private long toNumericIP(final String s) {
		final String[] parts = s.split("\\.");
		if (parts.length != 4) {
			throw new IllegalArgumentException("ip=" + s);
		}
		long n = Long.parseLong(parts[0]) << 24L;
		n += Long.parseLong(parts[1]) << 16L;
		n += Long.parseLong(parts[2]) << 8L;
		n += Long.parseLong(parts[3]);
		return n;
	}
}
