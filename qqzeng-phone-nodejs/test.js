var phoneF = require('../web/utils/PhoneUtil')

phoneF.load(__dirname + '/../data/qqzeng-phone.dat')

console.log(phoneF.query('1861840'))
