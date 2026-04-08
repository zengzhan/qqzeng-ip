#include "PhoneSearch.h"
#include <iostream>

int main()
{
	
	PhoneSearch* search = &PhoneSearch::getInstance();
	// 执行查询

	std::string phoneNumber = "1765811xxxx";
	std::string result = search->Query(phoneNumber);

	// 输出结果
	std::cout << result << std::endl;
	//山东|烟台|264000|0535|370600|联通


	return 0;
}
