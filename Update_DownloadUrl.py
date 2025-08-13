import requests
import sys
import secrets

sha = sys.argv[1]
version = f"{sys.argv[2]}-{sha}"

print (f"sha: {sha}")
print (f"version: {version}")

url = "https://fljpapi2-sjnq.onrender.com/qa/upload"
headers = {
    "Authorization": f"{secrets.PASSWORD}"
}
data = {
    "changelogUrl": "",
    "downloadUrl": f"https://github.com/Fortniteleakjp/FModel-JP-/releases/download/qa/{sha}.zip",
    "version": version
}

# 認証（パスワード方式）
response = requests.patch(url, headers=headers, json=data)

# 結果出力
if response.status_code == 200:
    print("completely updated Download Url for API")
else:
    print(f"updating Download Url for API failed : {response.status_code} - {response.text}")