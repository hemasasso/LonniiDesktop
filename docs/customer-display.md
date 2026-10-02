# Customer display (écran client)

Lonnii can show the customer what they are buying and what they owe. Configure it in
**Paramètres → Écran client** (admin). Settings are per machine, stored in
`%AppData%\Lonnii\customer-display.json`, never in the shared database.

| Kind | Hardware | How Lonnii sees it |
|---|---|---|
| Second screen | Laptop's other screen, 5–10" HDMI / USB-C panel | A normal monitor. **Auto-detect** uses the first screen that is not the main one, and starts or stops by itself when it is plugged in or removed. The view scales to any size or orientation and shows the item list, the total, the change, and a welcome screen (logo + QR code) when idle. |
| USB / serial | Pole display (2x20), Arduino / STM32 with an LCD | A COM port. Choose the port, speed, protocol and size. |
| Network | ESP32 (or anything) with an LCD, over WiFi | A TCP socket: host + port. |

Auto-detect only covers monitors. A COM port or an IP address cannot be told apart from a
printer or scanner without risking sending text to the wrong device, so those are chosen
once, then **Appliquer et tester** shows a sample sale.

## Text protocols

Text is plain ASCII (accents are removed), always exactly `columns` characters per row.

* **CD5220** — `ESC Q A <row 1> CR`, `ESC Q B <row 2> CR`. Most 2x20 pole displays.
* **ESC/POS** — for each row `US $ 0x01 <row number>` then the text. Epson-style displays.
* **Texte simple** — for your own hardware. A frame is:

  ```
  0x0C            form feed: start of a new frame, clear the display
  row 1 \n
  row 2 \n
  ...             as many rows as configured
  ```

### Arduino / ESP32 sketch (20x4 I2C LCD, USB serial)

```cpp
#include <LiquidCrystal_I2C.h>
LiquidCrystal_I2C lcd(0x27, 20, 4);
String line; int row = 0;

void setup() { Serial.begin(9600); lcd.init(); lcd.backlight(); }

void loop() {
  while (Serial.available()) {
    char c = Serial.read();
    if (c == 0x0C) { row = 0; line = ""; }                // new frame
    else if (c == '\n') { lcd.setCursor(0, row++); lcd.print(line); line = ""; }
    else line += c;
  }
}
```

For WiFi, listen with `WiFiServer server(9100)` and read from the connected `WiFiClient`
instead of `Serial` — the bytes are identical.
