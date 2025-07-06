import requests
import sys

sha = sys.argv[1]
version = sys.argv[2]

url = "https://fljpapi2-sjnq.onrender.com/qa/upload"
headers = {
    "Authorization": {secrets.PASSWORD}
}
data = {
    "changelogUrl": "https://github.com/Fortniteleakjp/FModel-JP-/releases/tag/qa",
    "downloadUrl": f"https://github.com/Fortniteleakjp/FModel-JP-/releases/download/qa/{sha}.zip",
    "version": version
}

# 認証（パスワード方式）
response = requests.patch(url, headers=headers, json=data)

# 結果出力
if response.status_code == 200:
    print("URL更新完了")
else:
    print(f"URL更新失敗 : {response.status_code} - {response.text}")