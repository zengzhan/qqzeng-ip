from phone_search import PhoneSearch6Db

def main():
    try:
        search = PhoneSearch6Db()
        
        # 测试示例
        print(search.query('1522008'))
        print(search.query('1588760'))
        print(search.query('1738907'))
        
        # 测试异常情况
        try:
            print(search.query('12345'))
        except ValueError as e:
            print(f"Expected error: {e}")

    except Exception as e:
        print(f"Error: {e}")

if __name__ == "__main__":
    main()



#广东|深圳|518000|0755|440300|中国移动
#云南|西双版纳|666100|0691|532800|中国移动
#西藏|阿里|859000|0897|542500|中国电信
