# Text recognition models

The ONNX models of Text recognition (docs/spec/ocr.md "Reading", "Packaging and dependencies"). They ship in the add-on
next to the server part (`Models/`); nothing is downloaded at run time. All are official PaddleOCR exports published by
PaddlePaddle on Hugging Face, unchanged, Apache-2.0 (license text with the attribution: `PaddleOCR-LICENSE.txt`, copied
into the plugin folder for the notices).

| File | Model | Source (Hugging Face, revision) | Size | SHA-256 |
|---|---|---|---|---|
| `det_mobile.onnx` | PP-OCRv5_mobile_det (text finder, CPU) | [PaddlePaddle/PP-OCRv5_mobile_det_onnx](https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det_onnx) `e6f4fa85f00e168c862bc462aebca69eef9b3d3d`, `inference.onnx` | 4,826,518 | `a431985659dc921974177a95adcfbb90fd9e51989a5e04d70d0b75f597b6e61d` |
| `det_server.onnx` | PP-OCRv5_server_det (text finder, graphics card and Accurate) | [PaddlePaddle/PP-OCRv5_server_det_onnx](https://huggingface.co/PaddlePaddle/PP-OCRv5_server_det_onnx) `dcf248c4dcc064c6d030db241255f9e8fbc6733a`, `inference.onnx` | 88,116,791 | `10803475a591f7dc623e24670fb5752ec94d39a1f8cf069aac1b6f0ce19cfc85` |
| `rec_latin_mobile.onnx` | latin_PP-OCRv5_mobile_rec (reader, every device) | [PaddlePaddle/latin_PP-OCRv5_mobile_rec_onnx](https://huggingface.co/PaddlePaddle/latin_PP-OCRv5_mobile_rec_onnx) `89d3a50e2c27e2e7cceeab0e944c25c807d5db4f`, `inference.onnx` | 8,042,023 | `7888113072263cb471b93f66dd5e2ad70548dc526fa1ace760d0d973dd121498` |
| `rec_latin_mobile.txt` | its character list: `PostProcess.character_dict` of the same revision's `inference.yml`, one entry per line (836) | same | 2,616 | `ccbcc45730b3fbbd9050c5bc74db6a99067141ef1035e3d14889a84a6b9b1aff` |
| `textline_ori.onnx` | PP-LCNet_x1_0_textline_ori (angle model: 0 or 180 degrees) | [PaddlePaddle/PP-LCNet_x1_0_textline_ori_onnx](https://huggingface.co/PaddlePaddle/PP-LCNet_x1_0_textline_ori_onnx) `7fdcf3cf7061163eda7183b224aa334bd33068f7`, `inference.onnx` | 6,777,816 | `38aa97cd4be591e0ad304e659f07ba30d946f27a63315433f6659c69c8778345` |

Downloaded 2026-11-03 from `https://huggingface.co/PaddlePaddle/<repo>/resolve/<revision>/inference.onnx`.

## Choices

- **No server recognition model.** PaddleOCR publishes no Latin PP-OCRv5 server recogniser (Latin exists as mobile only).
  The multilingual `PP-OCRv5_server_rec` (Chinese, English, Japanese; 84 MB, 18,383 characters) was measured on the
  accuracy set and read Latin text worse: it drops the spaces between words ("LOADINGDOCK3") and lost the signs group
  from 100 % to 62 %. So every device reads with `latin_PP-OCRv5_mobile_rec`; "the server models" means the server
  text finder.
- **The server text finder** finds small text the mobile one misses (hard receipts of the accuracy set 62.5 % against
  37.5 % exact), at about 6 times the work: on a graphics card always, on the CPU only for Accurate.
- **PP-LCNet x1.0** for the angle (99.4 % against 98.9 % for x0.25 in PaddleOCR's table, 6.8 MB). Its answer only names
  the candidates: a line it calls upside down is read both ways and the surer reading wins (it turned orange-on-blue print).
- A float16 conversion of the server text finder (half the size, twice as fast on DirectML) gave noise maps and is not used.

## Inputs and outputs

| Model | Input | Output |
|---|---|---|
| text finder | `[N, 3, H, W]`, H and W multiples of 32, blue-green-red, `(v / 255 - mean) / std` with mean 0.485, 0.456, 0.406 and std 0.229, 0.224, 0.225 per tensor channel | `[N, 1, H, W]` probability of text |
| reader | `[N, 3, 48, W]`, blue-green-red, `(v / 255 - 0.5) / 0.5`, lines padded with 0 to the widest (at least 320) | `[N, W / 8, 838]` probabilities: blank, the 836 entries, space |
| angle | `[N, 3, 80, 160]`, the line resized, normalised like the text finder | `[N, 2]`: 0 degrees, 180 degrees |
