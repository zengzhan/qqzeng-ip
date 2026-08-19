package abc.com.ip;



import java.io.IOException;

import org.junit.BeforeClass;
import org.junit.Test;

public class IPSearchTest {
	
	@Test
	public void test01(){
		try {
			IPSearch finder=new IPSearch();
			IPEntity t = finder.Query("123.4.5.6");
			System.out.println(t.getIp()+","+t.getCountry()+","+t.getSubInfo());
			
		} catch (IOException e) {
			e.printStackTrace();
		}
		
		
	}
	

}
