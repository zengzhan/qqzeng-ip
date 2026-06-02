import argparse
import csv
import os
from collections import defaultdict

from phone_search import PhoneSearch6Db


def parse_region_info(raw: str):
    parts = raw.split("|")
    if len(parts) != 6:
        return None
    return {
        "province": parts[0],
        "city": parts[1],
        "citycode": parts[2],
        "postcode": parts[3],
        "areacode": parts[4],
        "isp": parts[5],
    }


def ensure_dir(path: str):
    if not os.path.exists(path):
        os.makedirs(path, exist_ok=True)


def export_segments(db: PhoneSearch6Db, start_prefix: int, end_prefix: int, out_dir: str):
    detail_path = os.path.join(out_dir, "region_segments.csv")
    summary_path = os.path.join(out_dir, "region_summary.csv")

    summary = defaultdict(lambda: {"segment_count": 0, "estimated_phone_count": 0})
    total_segments = 0

    with open(detail_path, "w", newline="", encoding="utf-8") as detail_file:
        writer = csv.writer(detail_file)
        writer.writerow([
            "province",
            "city",
            "citycode",
            "postcode",
            "areacode",
            "isp",
            "segment7",
            "sample_phone",
            "estimated_full_phone_count",
        ])

        for prefix in range(start_prefix, end_prefix + 1):
            for sub in range(10000):
                segment7 = f"{prefix:03d}{sub:04d}"
                region_raw = db.query(segment7)
                if not region_raw:
                    continue

                info = parse_region_info(region_raw)
                if not info:
                    continue

                writer.writerow([
                    info["province"],
                    info["city"],
                    info["citycode"],
                    info["postcode"],
                    info["areacode"],
                    info["isp"],
                    segment7,
                    f"{segment7}0000",
                    10000,
                ])

                key = (
                    info["province"],
                    info["city"],
                    info["citycode"],
                    info["postcode"],
                    info["areacode"],
                    info["isp"],
                )
                summary[key]["segment_count"] += 1
                summary[key]["estimated_phone_count"] += 10000
                total_segments += 1

    with open(summary_path, "w", newline="", encoding="utf-8") as summary_file:
        writer = csv.writer(summary_file)
        writer.writerow([
            "province",
            "city",
            "citycode",
            "postcode",
            "areacode",
            "isp",
            "segment_count",
            "estimated_full_phone_count",
        ])

        for key, data in sorted(summary.items()):
            writer.writerow([
                key[0],
                key[1],
                key[2],
                key[3],
                key[4],
                key[5],
                data["segment_count"],
                data["estimated_phone_count"],
            ])

    print(f"Export completed. total_segments={total_segments}")
    print(f"Detail file: {detail_path}")
    print(f"Summary file: {summary_path}")


def main():
    parser = argparse.ArgumentParser(
        description="Export China phone segments grouped by region from qqzeng-phone-6.0.db"
    )
    parser.add_argument("--start-prefix", type=int, default=130, help="Start prefix, default 130")
    parser.add_argument("--end-prefix", type=int, default=199, help="End prefix, default 199")
    parser.add_argument("--out-dir", default="output", help="Output directory, default ./output")
    args = parser.parse_args()

    if args.start_prefix < 0 or args.end_prefix > 199 or args.start_prefix > args.end_prefix:
        raise ValueError("Invalid prefix range. Must satisfy 0 <= start <= end <= 199")

    ensure_dir(args.out_dir)
    db = PhoneSearch6Db()
    export_segments(db, args.start_prefix, args.end_prefix, args.out_dir)


if __name__ == "__main__":
    main()
