# Mốc triển khai — 2026-11-24

Đây là mốc bàn giao code hiện tại, ghi theo ngày thực hiện. Nội dung này không gán ngày hoặc lịch sử cho các tuần trước.

## Đã triển khai

- Hoàn thiện solution .NET 10 gồm Identity, Document, Approval, Notification và Blazor Client.Web.
- Hoàn tất EF migrations, database riêng cho từng backend, private object storage, role/resource authorization, workflow tuần tự và giao diện quản lý.
- Hoàn tất RabbitMQ, transactional Outbox/Inbox, notification, retry và khôi phục consumer khi broker restart.
- Hoàn tất Dockerfile, Compose, health checks, `.env.example`, README và báo cáo.
- Bổ sung test đồng thời cho quyết định cùng task và Document API upload/download/ownership; toàn bộ test đạt 15/15.
- Sửa xung đột CSS mẫu Blazor khiến sidebar chiếm luồng trang và đẩy nội dung xuống dưới; dựng lại Client.Web, xác nhận dashboard căn đúng bằng browser.
- Hoàn thiện hai mục UI còn thiếu khi đối chiếu đặc tả: lọc tài liệu theo ngày tạo và hiển thị số thông báo chưa đọc trên dashboard cho mọi vai trò.
- Chạy thêm một workflow Approved hai cấp và một workflow Rejected có lý do qua UI thật; xác nhận hai quyết định, bước sau `Skipped`, và notification tới Creator.

## Bằng chứng chạy

- `dotnet build DistributedDocApproval.slnx --no-restore -v:q`: thành công, 0 warning, 0 error.
- `dotnet test DistributedDocApproval.slnx --no-restore -v:q`: chạy lại sau cập nhật UI, 15 passed (8 unit + 7 integration), 0 failed, 0 skipped.
- `docker compose ... build`: năm image ứng dụng được build; tám service đều healthy.
- Sau cập nhật UI, `docker compose ... build client-web` thành công; recreate service healthy và `/login` trả HTTP 200.
- Clone sạch từ GitHub `main`: build thành công, 15/15 tests, Compose config hợp lệ, tám container healthy, bốn API readiness và trang login đạt; stack kiểm chứng riêng đã được dọn.
- Smoke test đã chạy luồng Approved hai cấp, Rejected, cookie login HttpOnly, outage Notification API và RabbitMQ. Kịch bản chi tiết ở [TEST_RESULTS.md](TEST_RESULTS.md).
- Sau sửa layout, Client.Web Docker build thành công, container healthy; dashboard Creator, inbox hai Approver, hai timeline và notifications được chụp từ phiên browser đã đăng nhập.
- RabbitMQ Management hiển thị sáu queue đang chạy; snapshot container, readiness API và queue state ở [runtime-status.txt](runtime-status.txt). Danh sách ảnh và khoảng trống còn lại ở [screenshots README](../06-screenshots/README.md).
- Tạm dừng Notification API trước khi submit; workflow hai cấp vẫn Approved, ba event được giữ trong RabbitMQ và được consume sau restart. Health trở lại healthy, queue về 0, Creator nhận thông báo kết quả.
- Bằng chứng outage/recovery gồm `docker compose ps --all` trước/sau, RabbitMQ queue trước/sau và log Notification API; log lọc theo document/correlation ID ở [notification-recovery.log](notification-recovery.log).

## GitHub

- Commit `2353a6bf869403fb538c888d378b9fa7908c3640` (`feat: complete distributed document approval MVP`) được push lên `refs/heads/main`.
- Commit `fe287f9` ghi nhật ký mốc và `6cd22ad` ghi bằng chứng clone sạch cũng đã push.
- Commit `d8a4739b74db475e1a79e2afee99232adc8d6068` (`fix: correct dashboard layout and add runtime evidence`) và commit tài liệu `44513dd6ff9ce9f245bafe398d038c26b5b1f56c` đã được push trước đợt rà soát này.
- Đầu đợt rà soát, `git ls-remote` xác nhận `refs/heads/main` trên GitHub trỏ tới `44513dd6ff9ce9f245bafe398d038c26b5b1f56c`.
- Commit UI `fad6c02c2688485914dd3b547ecec5ad474bd25b` (`feat: complete dashboard and document filters`) được push lên `main`; đây là commit chứa bộ lọc ngày tạo và số thông báo chưa đọc.
- Tác giả Git: `Tuan <tuanvik206@gmail.com>`.
- Không tạo commit hoặc ngày lịch sử giả cho các tuần trước; mốc tiếp theo cần ghi theo ngày thực hiện.

## Ghi chú minh chứng

- Ảnh Compose thể hiện output live `docker compose ps --all` được render trong trang local để dễ đọc, không phải giao diện Docker Desktop. Workflow/timeline, notification, RabbitMQ queues và chuỗi outage/recovery đều có ảnh runtime.
- Runtime snapshot chi tiết ở [runtime-status.txt](runtime-status.txt); log recovery ở [notification-recovery.log](notification-recovery.log).

Khi mỗi mốc tiếp theo hoàn tất, thêm một mục mới có ngày thực hiện, thay đổi, lệnh kiểm tra, ảnh và commit SHA thực tế.
