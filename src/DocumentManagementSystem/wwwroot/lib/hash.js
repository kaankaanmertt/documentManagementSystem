// SHA-256 wrapper — SubtleCrypto kullanır, hex (uppercase) döner.
// Server tarafında System.Security.Cryptography.SHA256 ile aynı çıktı verir.

(function (global) {
    async function sha256Hex(text) {
        const data = new TextEncoder().encode(text);
        const buf = await crypto.subtle.digest('SHA-256', data);
        const bytes = new Uint8Array(buf);
        let hex = '';
        for (let i = 0; i < bytes.length; i++) {
            hex += bytes[i].toString(16).padStart(2, '0');
        }
        return hex.toUpperCase();
    }
    global.sha256Hex = sha256Hex;
})(window);
