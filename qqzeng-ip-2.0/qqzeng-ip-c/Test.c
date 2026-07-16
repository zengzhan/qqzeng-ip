#include "IpSearch2Fast.h"
#include <locale.h>
#include <time.h>
int main(int argc, char** argv)
{
	IpSearch2Fast* ipSearch = getInstance();
	if (!ipSearch)
	{
		printf("the IPSearch instance is null!");
		return -1;
	}
	
	char* ip = "58.213.198.68";
	char* local = geoip_query(ipSearch, ip);
	setlocale(LC_ALL, "zh_CN.UTF-8"); // 设置语言区域为中文
	printf("%s\n", local);
	//亚洲|中国|江苏|南京|秦淮|电信|320104|China|CN|118.79815|32.01112

	system("pause");
	return 0;
}