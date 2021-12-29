#include "GeoIP.h"
int main(int argc, char **argv)
{
	geo_ip *finder = geoip_instance();
	if (!finder)
	{
		printf("the IPSearch instance is null!");
		return -1;
	}	
	char *ip = "0.0.0.0";
	char *local = geoip_query(finder, ip);
	printf("%s\n",local);
    getchar();
	return 0;
}