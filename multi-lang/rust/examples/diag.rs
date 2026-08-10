use qzdb_reader::QzdbReader;
use std::path::PathBuf;
fn main() {
    let p = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data/qqzeng_ip_std_china.qzdb");
    let base = std::fs::read(p).unwrap();
    let ru64 = |b: &[u8], o: usize| { let mut a=[0u8;8]; a.copy_from_slice(&b[o..o+8]); u64::from_le_bytes(a) };
    let mut b = base.clone();
    let ors = ru64(&b,40) as usize;
    let ogs = ru64(&b,48) as usize;
    let oir = ru64(&b,96) as usize;
    let oge = ru64(&b,104) as usize;
    println!("row_schema={} group_schema={} ip_row={} geo_entries={}", ors,ogs,oir,oge);

    // 先看未改动时能查到什么
    let r0 = QzdbReader::from_bytes(&base, 0, false).unwrap();
    println!("baseline find(1.0.1.1) = {:?}", r0.find("1.0.1.1").map(|g| g.to_pipe()));
    println!("baseline row_id(1.0.1.1) = {:?}", r0.lookup_row_id("1.0.1.1"));

    b[160..164].copy_from_slice(&4u32.to_le_bytes());
    b[ors]=1; b[ors+1]=4; b[ors+4]=0; b[ors+5]=4;
    b[ogs+10..ogs+14].copy_from_slice(&u32::MAX.to_le_bytes());
    b[oge+2..oge+6].copy_from_slice(&u32::MAX.to_le_bytes());
    b[168..174].copy_from_slice(&0xFFFF_FFFF_FFFFu64.to_le_bytes()[..6]);
    let end = oge.min(b.len());
    let mut p2 = oir;
    while p2+4 <= end { b[p2..p2+4].copy_from_slice(&0xFFFF_FFFEu32.to_le_bytes()); p2+=4; }

    match QzdbReader::from_bytes(&b, 0, false) {
        Err(e) => println!("LOAD REJECTED: {:?}", e),
        Ok(r) => {
            println!("LOAD OK");
            println!("  row_id(1.0.1.1) = {:?}", r.lookup_row_id("1.0.1.1"));
            println!("  find(1.0.1.1)   = {:?}", r.find("1.0.1.1").map(|g| g.to_pipe()));
        }
    }
}
