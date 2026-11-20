---
name: sonca-offline-testing
description: Chạy và phát triển bộ kiểm thử ngoại tuyến (offline test suite) cho SoncaAudioInspector mà không cần phần cứng âm thanh, micro vật lý hay sóng BLE thật. Dùng khi kiểm tra regression sau khi sửa mã nguồn hoặc bổ sung ca kiểm thử mới.
---

# Kiểm thử ngoại tuyến SoncaAudioInspector (Offline Testing)

Tài liệu liên quan: [../../../AUTO_TEST_PERFORMANCE.vi.md](../../../AUTO_TEST_PERFORMANCE.vi.md), [../../../docs/CONFIG_TESTS.vi.md](../../../docs/CONFIG_TESTS.vi.md).

## Nguyên tắc
- Chạy qua script điều phối: `scripts/run-offline-checks.ps1`.
- Không yêu cầu thiết bị âm thanh, radio BLE, network server thực tế (mọi kiểm thử chạy trên synthetic data hoặc recorded fixtures).
- Mọi kết quả, log và binary build được lưu độc lập vào thư mục artifact (`artifacts/YYYYMMDD-HHmmss-fffZ-offline-checks/`) tạo bởi `scripts/new-artifact.ps1`.
- Giữ sạch working tree; không tạo thư mục tạm tại repo root.
- Cần hiểu code trong các harness: tra cứu CodeGraph trước.

## Cách chạy

1. **Chạy toàn bộ bộ test offline (mặc định các test không gắn thẻ slow hay quarantine):**
```powershell
$root = git rev-parse --show-toplevel
powershell.exe -ExecutionPolicy Bypass -File "$root/scripts/run-offline-checks.ps1"
```

2. **Chạy lọc theo nhóm hoặc mã bài test cụ thể (-Only):**
```powershell
# Chạy nhóm audio logic và update
powershell.exe -ExecutionPolicy Bypass -File "$root/scripts/run-offline-checks.ps1" -Only audio,update

# Chạy đích danh các bài kiểm thử
powershell.exe -ExecutionPolicy Bypass -File "$root/scripts/run-offline-checks.ps1" -Only measurement-check,update-logic,ui-source-layout
```

3. **Liệt kê danh mục bài test (-List):**
```powershell
powershell.exe -ExecutionPolicy Bypass -File "$root/scripts/run-offline-checks.ps1" -List
```

4. **Bao gồm các test chạy chậm (slow) như distortion audit, sweep analysis:**
```powershell
powershell.exe -ExecutionPolicy Bypass -File "$root/scripts/run-offline-checks.ps1" -IncludeSlow
```

## Các harness kiểm thử chính
- `AudioLifecycleCheck`: Kiểm tra logic chọn reference, clock drift correction, sweep equivalence, QA graph lifecycle, update logic.
- `FeedbackUiCheck`: Kiểm tra giao diện WPF không cần hiển thị màn hình thực (headless/offscreen rendering, theme loading, version label binding, button states).
- `MeasurementCheck`: Kiểm tra phân tích méo hài, suy hao, chẩn đoán âm lượng/đáp tuyến qua shim offline.
- `StandardMeasurementCheck`: FFT biến đổi tần số, calibration open/3-point, IMD, compression, polarity.
- `ServerConfigCheck`: Xác thực shape manifest và quy tắc cấu hình trạm đo.
- `PinkNoiseCheck`: Kiểm tra bộ phát pink noise, xuất WAV và settings.
- `scripts/check-source-layout.ps1`: Đảm bảo quy hoạch cây thư mục mã nguồn và XAML hợp lệ.
- `scripts/check-skills.ps1`: Xác thực frontmatter, link và runbook trong thư mục `.agents/skills`.
- `scripts/check-artifact-scripts.ps1`: Kiểm tra an toàn sandbox cho quản lý artifact.
