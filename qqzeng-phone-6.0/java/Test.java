public class Test {
    public static void main(String[] args) {
        PhoneSearch6Db db = PhoneSearch6Db.getInstance();
        String[] testNumbers = {
            "1522008", "1345555", "1386130", 
            "1579441", "1888888", "1999957"
        };

        for (String number : testNumbers) {
            String result = db.query(number);
            System.out.printf("%s -> %s%n", number, (result != null) ? result : "Not Found");
        }
    }
}
