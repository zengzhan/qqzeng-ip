#ifndef PHONESEARCH4BINARY_H
#define PHONESEARCH4BINARY_H

#include <string>
#include <unordered_map>
#include <memory>
#include <vector>
#include <mutex>

class PhoneSearch4Binary {
private:
   
   
    static PhoneSearch4Binary* instance;
    static std::mutex mutex;


    std::unique_ptr<std::vector<unsigned char>> data;
    std::unordered_map<std::string, int> prefixDictionary;
    std::vector<std::string> addressArray;
    std::vector<std::string> ispArray;

    std::size_t BytesToInt(const std::unique_ptr<std::vector<unsigned char>>& bytes, std::size_t startIndex, std::uint8_t size);
    std::vector<std::string> SplitStringArray(const std::unique_ptr<std::vector<unsigned char>>& bytes, size_t start, size_t len);

    PhoneSearch4Binary() {
      
        LoadData();
    };

public:
  
    static PhoneSearch4Binary* getInstance() {
        if (instance == nullptr) {
            std::lock_guard<std::mutex> lock(mutex);
            if (instance == nullptr) {
                instance = new PhoneSearch4Binary();
            }
        }
        return instance;
    }

    void LoadData();
  
    std::string Query(const std::string& phoneNumber);
};

#endif // PHONESEARCH4BINARY_H
