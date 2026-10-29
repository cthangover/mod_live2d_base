# Live2D Cubism — nhân vật 2D chuyển động

Live2D Cubism mang mô hình Live2D vào trò chơi: những nhân vật được vẽ, biết thở, chớp mắt, dõi theo một điểm, chuyển động theo vật lý (tóc, quần áo) và diễn các chuyển động cùng biểu cảm mà họa sĩ đã làm. Mod chứa runtime Cubism cho C# cùng các thư viện lõi gốc của nó; các mod khác dùng nó để hiện một mô hình trong cảnh.

Đây là một khối tùy chọn của trò chơi Cthangover. Nội dung cốt truyện hiện tại vẫn dùng chân dung tĩnh, và chưa trò chơi tích hợp nào kèm mô hình Live2D; bản demo riêng của mod cho thấy một mô hình đang chạy.

![Mô hình mẫu Ren do mod này kết xuất: hai tay buông xuôi, và sau khi nâng một tham số (ParamArmR01) thì cánh tay phải giơ lên.](assets/arm-parameter.jpg)

## Một mô hình có thể làm gì

- **Sự sống khi rảnh rỗi** — tự động chớp mắt (bật mặc định) và thở (tắt mặc định).
- **Vật lý** — tóc và quần áo đung đưa theo `physics3.json` của mô hình.
- **Ánh nhìn** — đầu và mắt xoay về một điểm đích; bản demo cho phép kéo chuột trên nhân vật để hướng ánh nhìn.
- **Chuyển động và biểu cảm** — phát một chuyển động thuộc một nhóm (`mtn_01`…) hoặc đặt một biểu cảm (`exp_01`…) được định nghĩa trong mô hình.
- **Tham số** — đặt trực tiếp bất kỳ tham số nào của mô hình, như trong hình trên, nơi một tham số nâng cánh tay phải lên.
- **Bố trí** — mô hình tự khớp vào vùng của nó, hoặc được đặt ở vị trí và tỉ lệ cho trước.

## Yêu cầu của mô hình

Một mô hình là thư mục xuất chuẩn của Cubism, được giữ bên trong một mod:

```filestree
my_mod/
└── models/
    └── runtime/
        ├── ren.model3.json       tệp dẫn nhập mà bạn trỏ tới
        ├── ren.moc3
        ├── ren.physics3.json
        ├── ren.cdi3.json
        ├── ren.4096/texture_00.png
        ├── expressions/exp_01.exp3.json …
        └── motions/mtn_01.motion3.json …
```

- Mô hình là mô hình Cubism (`.moc3` với một `model3.json`). Mô hình demo được làm bằng Cubism 5.3 và dùng mặt nạ cùng cách vẽ ngoài màn hình của nó.
- Lõi Cubism gốc được kèm cho Windows (`.dll`) và Linux (`.so`).
- Các mô hình mẫu đi kèm giấy phép riêng của Live2D; hãy đọc `ReadMe.txt` của mô hình trước khi công bố nó.

## Hiện mô hình từ kịch bản

Hành động `add_live2d_model` đặt một mô hình vào khung nhìn của cảnh:

```scenario
action add_live2d_model model_path=models/runtime/ren.model3.json mod_id=my_mod node_name=Ren x=960 y=540 scale=0.5
```

- `model_path` — tệp `model3.json`, đường dẫn tương đối so với thư mục mod;
- `mod_id` — mod chứa nó;
- `node_name` — tên của mô hình này trong cảnh (hành động thứ hai trùng tên sẽ bị bỏ qua);
- `x`, `y`, `scale` — vị trí và kích thước tùy chọn; không có chúng thì mô hình tự khớp.

Một mod có mã có thể tạo mô hình bằng `Live2DService.CreateModel(...)` rồi gọi `StartMotion(group, number)`, `StartExpression(id)`, `SetLiveParameter(id, value)` hoặc `SetTargetPoint(x, y)` trên nó.

## Bản demo

Bộ kiểm thử **avatar** của mod chứa mô hình mẫu Ren. Khi bật, Ren xuất hiện bên ngoài biệt thự ở Cthangover và đi theo thao tác kéo chuột; phần kiểm tra tự động của nó nâng cánh tay phải và so sánh các khung hình. Đây là bản demo dành cho nhà phát triển và tắt trong lối chơi bình thường.

## Khi có gì đó không ổn

1. **Không có mô hình và log nhắc tới `model_path` hoặc mod.** Đường dẫn tính từ thư mục mod và phải trỏ tới `model3.json`; `mod_id` phải là id của mod chứa nó.
2. **Mô hình hiện sai chỗ hoặc sai kích thước.** Hãy cho `x`, `y` và `scale`, hoặc bỏ cả ba để mô hình tự khớp.
3. **Mô hình không tải được trên một nền tảng.** Thư viện lõi Cubism chỉ có cho Windows và Linux.
