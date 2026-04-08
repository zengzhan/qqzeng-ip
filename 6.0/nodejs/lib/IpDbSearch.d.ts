export class IpDbSearch {
    static getInstance(): IpDbSearch;
    find(ip: string): string;
}
