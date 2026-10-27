import sqlite3, json, sys
db = r"C:\Users\Administrator\AppData\Local\VoWin\preferences.db"
con = sqlite3.connect(db)
cur = con.cursor()
cur.execute("UPDATE SimPreferences SET DedicatedProxyUrl=? WHERE Iccid=?", ("socks5h://127.0.0.1:10801", "89441000400316382888"))
print("sim updated:", cur.rowcount)
prof = {
  "Model": "iPhone 18 Pro",
  "UserAgent": "iPhone Pro/18",
  "Tac": "35499626",
  "Imei": "354996261234567",
  "Latitude": 51.5074,
  "Longitude": -0.1278,
  "Timezone": "Europe/London"
}
cur.execute("UPDATE ModulePreferences SET DeviceProfile=? WHERE Id=?", (json.dumps(prof, ensure_ascii=False), "pcsc-b6ad3a6866d0"))
print("module updated:", cur.rowcount)
con.commit()
cur.execute("SELECT Iccid, DedicatedProxyUrl, DefaultVoWifi FROM SimPreferences WHERE Iccid=?", ("89441000400316382888",))
print("sim row:", cur.fetchone())
cur.execute("SELECT Id, DeviceProfile FROM ModulePreferences WHERE Id=?", ("pcsc-b6ad3a6866d0",))
print("module row:", cur.fetchone()[1])
con.close()
