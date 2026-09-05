# UAC audio: iso packets COMPLETE but actual_length=0 — analysis

State (commit 5c873b5): from-scratch libusb iso capture. Packet stats are
healthy — steady ~1024 iso packets/s, all LIBUSB_TRANSFER_COMPLETED — but
every packet is 0 bytes. The device is not putting audio on the wire.

## 2026-09-06 — leading theory is now #5 (video-gating), acted on

Re-reading the record: the Android HAL (snd-usb-audio/PAL) **also** fails to
read this card's iso IN — `SessionAlsaPcm: read: Failed to read data` on
every read, format negotiated fine. So it is NOT "the HAL knows a trick our
code doesn't". Both paths get nothing. Yet **HDMI Link gets audio** — so the
card *can* stream iso audio on this Quest.

The "audio-only window" diagnostic in NuvcCapture (hold video off 10s at
launch, start audio inside that window) was built to rule out bus
contention. It did — but it also **guaranteed audio always started with no
video stream active**. Every zero-bytes observation so far was taken with
video either off or not yet COMMIT-ed.

Hypothesis #5 (this card muxes audio only while its HDMI-capture pipeline is
live) fits every data point and hasn't actually been tested. **Change made
(NuvcCapture.cs):** removed the audio-only window; audio now starts only
after video is streaming real frames (`Seq > 0 && _framesSeen > 30`). Plus
the uac.cpp diagnostics (descriptor dump, kernel-driver state, GET_INTERFACE
confirm) from commit 5641d5a. Next hands-on session: one run tells us
whether ordering fixes it, and if not the dump says why.

**Crucial clue: AudioRecord DID get real 48 kHz audio from this same card
earlier** (commit fa42be7) — though later notes suggest that may have been
"the pipe opened at 48k", not "real samples confirmed". Treat as weak.

## Ranked hypotheses

### 1 — scan() picks the WRONG AudioStreaming alt setting
`scan()` takes the **first** AS interface alt that has a non-zero-bandwidth
iso IN endpoint.
**Weakened 2026-09-06:** the recorded descriptors show AS interface IF3 has
only alt 0 (idle) and alt 1 (ep 0x82 IN iso ASYNC, 192 B/1ms, 2ch/16/48000)
— no other alt to pick wrong. scan() picking alt 1 is correct. The
descriptor dump will confirm there's genuinely no second AS interface.
The HAL picks the alt whose FORMAT_TYPE matches the requested 48 kHz/2ch.

**Do:** dump every AS alt with its FORMAT_TYPE_I descriptor
(`bNrChannels`, `bBitResolution`, `bSamFreqType`, the tSamFreq list) and its
endpoint (`wMaxPacketSize`, `bInterval`). Then select the alt matching
48000/2/16 explicitly, not `[0]`.

### 2 — FORMAT_TYPE descriptor parsed wrong
`scan()` does `rate = p[8] | p[9]<<8 | p[10]<<16` — that's only correct for
`bSamFreqType == 1` (one discrete rate). If `bSamFreqType == 0` (continuous:
tLowerSamFreq at p[8..10], tUpperSamFreq at p[11..13]) or `> 1` (a list),
p[8..10] is either the lower bound or just the first of several. We may be
locking onto e.g. 44100 when the stream is 48000.

**Do:** branch on `bSamFreqType`. For a list, choose 48000 if present. For
continuous, SET_CUR 48000 on the endpoint (see #3).

### 3 — sampling-freq SET_CUR skipped when it's actually required
`start()` only sends SET_CUR SAMPLING_FREQ_CONTROL when the endpoint
descriptor's `bmAttributes bit0` is set. Plenty of cheap chips leave that
bit clear but still need the rate set before they'll stream — and the HAL
sends it unconditionally for any multi-rate / continuous format.

**Do:** send SET_CUR sampling freq to the **endpoint** (request type 0x22,
wValue = SAMPLING_FREQ_CONTROL<<8, wIndex = ep_in_) whenever the format has
more than one rate OR is continuous, regardless of bit0. Verify the 3-byte
payload is `rate` little-endian 24-bit.

### 4 — the two USB handles fight over the interface
Audio uses a **second** `openDevice()` + its own libusb_context on the same
physical card that uvc.cpp already has open. If uvc.cpp claims the whole
config, or claims interface numbers that overlap the audio AC/AS interfaces,
then `libusb_claim_interface` / `libusb_set_interface_alt_setting` on the
audio side can return success while the device ignores the alt change
(another handle owns the interface state).

**Do:** log which interfaces uvc.cpp claims. Confirm they're only the VideoControl
+ VideoStreaming numbers, never the two Audio interface numbers. Consider
doing audio on the **same** UsbDeviceConnection/libusb handle as video
instead of a second openDevice.

### 5 — card only muxes audio while the video stream is actively pulling
Some dongles gate the UAC payload on the UVC stream being in COMMIT/streaming
state. If the from-scratch audio path starts before/without video actively
streaming frames, audio stays silent.

**Do:** confirm video is actively delivering frames (it is: 60/60) at the
moment audio transfers are submitted. Order: start video → wait for first
frame → then start audio.

### 6 — HDMI source has no audio
Switch 2 HDMI audio setting, or the TV/monitor in the chain stripping it.
Least likely (AudioRecord got audio) but check the Switch's audio output
setting and that nothing in the HDMI path is HDCP-renegotiating.

## Fastest path

1. `bash _dev/rt.sh` the card in, get a full lsusb-style descriptor dump of
   the AC + all AS interfaces (write a one-shot dump in uac.cpp scan() that
   LI()s every altsetting + every CS descriptor byte range).
2. Get logcat from a working AudioRecord run in parallel — grep for the
   HAL's alt-setting / sample-rate choice (`UsbHostManager`, `alsa_utils`,
   `audio_hw`, `AudioStreamIn`).
3. Make uac.cpp select the same alt + send the same SET_CUR the HAL sends.

Do NOT change uac.cpp blindly without the descriptor dump — the alt indices
and format layout are device-specific and guessing wastes a battery charge.
