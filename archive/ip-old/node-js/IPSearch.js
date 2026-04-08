var fs = require('fs'),
	iconv = require('iconv-lite'),
	relation = {}, partion = {};


var geodat = null, 
	indexFirst = 0, 
	indexLast = 0,
	ipRegexp = /^(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])\.(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])\.(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])\.(\d{1,2}|1\d\d|2[0-4]\d|25[0-5])$/;

function ipToInt32(ip){
	var result = ipRegexp.exec(ip);
	if(result){
		return parseInt(result[1])*16777216
			+ parseInt(result[2])*65536
			+ parseInt(result[3])*256
			+ parseInt(result[4]);
	}
	return false;
}

function searchIndex(intip){
	var index_top = indexLast, 
		index_bottom = indexFirst,
		record,
		index_current = parseInt((index_top - index_bottom)/7/2)*7+index_bottom;	
	do{
		record = geodat.readUInt32LE(index_current);
		if(record > intip){
			index_top = index_current;
			index_current = parseInt((index_top-index_bottom)/14)*7+index_bottom;  
		}else{
			index_bottom = index_current;  
			index_current = parseInt((index_top-index_bottom)/14)*7+index_bottom;  
		}
	}while(index_bottom<index_current);	
	
	return geodat.readUInt32LE(index_current+4)%16777216;
}

function pushString(array, addr){
	if(addr==0){
		array.push('未知');
		return 0;
	}
	var buf = new Buffer(255);
	var stringEnd = addr;
	while(geodat[stringEnd]){
		stringEnd++;
	}
	array.push(iconv.decode(geodat.slice(addr, stringEnd), 'GBK'));
	return stringEnd;
}

// 加载IP数据库
try{
	geodat = fs.readFileSync(__dirname+'/data/qqzeng-ip.dat');
	if(geodat){
		indexFirst = geodat.readUInt32LE(0);
		indexLast = geodat.readUInt32LE(4);
	}
}catch(err){
	console.error(err);
}


//查询IP
exports.getAddress = function(ip){
	var intip = ipToInt32(ip);
	if(intip){
		var addr = searchIndex(intip),
			redirectMode = geodat.readUInt8(addr+=4), redirectAddr, tmpAddr, geo = [];
		if(redirectMode == 1){
			redirectAddr = geodat.readUInt32LE(addr+1)%16777216;
			if(geodat.readUInt8(redirectAddr) == 2){
				pushString(geo, geodat.readUInt32LE(redirectAddr+1)%16777216);
				tmpAddr = redirectAddr + 4;
			}else{
				tmpAddr = pushString(geo, redirectAddr)+1;
			}
		}else if(redirectMode == 2){
			pushString(geo, geodat.readUInt32LE(addr+1)%16777216);
			tmpAddr = addr+4;
		}else{
			tmpAddr = pushString(geo, addr)+1;
		}
		redirectMode = geodat.readUInt8(tmpAddr);
		if(redirectMode == 2 || redirectMode == 1){
			pushString(geo, geodat.readUInt32LE(tmpAddr+1)%16777216);
		}else{
			pushString(geo, tmpAddr);
		}
		return geo;
	}
	return ['error',''];
}




//调用方法  
var lbs = require('./ipsearch.js');
lbs.getAddress(ip);