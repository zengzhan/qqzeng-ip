import IpDbSearch from './IpDbSearch.js';

export default IpDbSearch;
export { IpDbSearch };
export const getInstance = IpDbSearch.getInstance.bind(IpDbSearch);
export const fromBytes = IpDbSearch.fromBytes.bind(IpDbSearch);
export const findDbPath = IpDbSearch.findDbPath.bind(IpDbSearch);
export const fastParseIp = IpDbSearch.fastParseIp.bind(IpDbSearch);
