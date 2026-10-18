using System.Collections.Generic;
using System.IO;
using Cthangover.Core.Mods;
using Cthangover.Core.Utils;
using Cthangover.Live2D.Cubism.Framework;
using Cthangover.Live2D.Cubism.Services;
using Godot;

namespace Mods.Live2DTest
{
    /// <summary>
    /// Интеграционный тест Live2D-аватара: создаёт модель через мод-пайплайн
    /// (тот же путь, что и <see cref="Live2DOverlay"/>), держит руки опущенными
    /// секунду, затем поднимает правую руку через <c>ParamArmR01</c> и
    /// проверяет смену позы компьютерным зрением по скриншотам вьюпорта:
    /// <list type="bullet">
    ///   <item>детерминизм — два кадра статичной позы идентичны попиксельно;</item>
    ///   <item>видимость — команда смены позы перекрашивает значимую часть кадра;</item>
    ///   <item>силуэт — в верхнем боковом секторе (зона над плечом) площадь
    ///       аватара растёт после подъёма руки;</item>
    ///   <item>удержание — параметр держит поднятое значение между кадрами
    ///       (регрессия на Load/SaveParameters-брекет).</item>
    /// </list>
    /// Скриншоты сохраняются в tests/artifacts/live2d_avatar. Инстанцируется
    /// игровой интеграционной сценой через TscnScenes (мод — единственное место
    /// с компайл-тайм доступом к Live2D-обёртке); сам печатает [TEST_RESULT]
    /// и завершает дерево с кодом выхода.
    /// </summary>
    public partial class Live2DAvatarTestDriver : Node2D
    {
        private const string ModId = "live2d_base__avatar";
        private const string ModelPath = "models/runtime/ren.model3.json";
        private const string LeftArmParam = "ParamArmL01";
        private const string RightArmParam = "ParamArmR01";

        /// <summary>Сколько держим опущенные руки до скриншота (по ТЗ — секунду).</summary>
        private const float LoweredHoldSeconds = 1.0f;

        /// <summary>Пауза между кадрами проверки детерминизма.</summary>
        private const float StabilityGapSeconds = 0.4f;

        /// <summary>Ожидание растеризации поднятой руки.</summary>
        private const float RaisedSettleSeconds = 0.8f;

        /// <summary>Порог «далёкий от фона» пиксель силуэта, 0..1.</summary>
        private const float SilhouetteTolerance = 0.2f;

        /// <summary>Порог значимого изменения канала для diff-подсчёта.</summary>
        private const float DiffTolerance = 0.1f;

        private int passed;
        private int failed;
        private string artifactDir;

        public override void _Ready() => _ = RunAsync();

        private async System.Threading.Tasks.Task RunAsync()
        {
            try
            {
                GameLogger.Log("TEST", "Live2DAvatarTestDriver: starting Live2D avatar integration test");
                artifactDir = ProjectSettings.GlobalizePath("res://tests/artifacts/live2d_avatar");
                Directory.CreateDirectory(artifactDir);

                await RunTest();
            }
            catch (System.Exception ex)
            {
                Fail($"unexpected crash: {ex.GetType().Name}: {ex.Message}");
                GameLogger.Log("TEST", ex.StackTrace ?? "(no stack)");
            }

            GameLogger.Log("TEST", $"Live2DAvatarTestDriver: {passed} passed, {failed} failed");
            GameLogger.Log("TEST", $"[TEST_RESULT] name=live2d_avatar passed={passed} failed={failed}");
            GetTree().Quit(failed == 0 ? 0 : 1);
        }

        private async System.Threading.Tasks.Task RunTest()
        {
            Pass($"driver started for mod '{ModId}'");


            // Тот же путь создания, что в продовом оверлее; моргание выключено,
            // чтобы кадры были пиксельно детерминированы.
            var model = Live2DService.CreateModel(this, new Live2DModelConfig
            {
                ModelPath = ModelPath,
                ModId = ModId,
                NodeName = "Live2DTestAvatar",
                EnableEyeBlink = false
            });

            if (model == null || model.Parameters.Length == 0)
            {
                Fail("avatar creation failed (no parameters)");
            }
            Pass($"avatar created: {model.Parameters.Length} parameters, canvas {model.CanvasWidth:F0}x{model.CanvasHeight:F0}");

            var leftArm = model.FindParameter(LeftArmParam);
            var rightArm = model.FindParameter(RightArmParam);
            if (leftArm == null || rightArm == null)
            {
                Fail($"arm parameters not found ('{LeftArmParam}' / '{RightArmParam}')");
                return;
            }
            Pass($"{LeftArmParam} [{leftArm.MinimumValue}..{leftArm.MaximumValue}], {RightArmParam} [{rightArm.MinimumValue}..{rightArm.MaximumValue}]");

            // Фаза 1 — руки опущены, держим ~1 секунду.
            model.SetLiveParameter(LeftArmParam, leftArm.MinimumValue);
            model.SetLiveParameter(RightArmParam, rightArm.MinimumValue);
            await Delay(LoweredHoldSeconds);

            var lowered = await Capture();
            await Delay(StabilityGapSeconds);
            var stability = await Capture();

            // Фаза 2 — поднимаем руку.
            model.SetLiveParameter(RightArmParam, rightArm.MaximumValue);
            await Delay(RaisedSettleSeconds);
            var raised = await Capture();

            SavePng("01_lowered.png", lowered);
            SavePng("02_lowered_stability.png", stability);
            SavePng("03_raised.png", raised);

            Analyze(model, lowered, stability, raised);
        }

        /// <summary>
        /// Компьютерное зрение по трём кадрам: детерминизм статичной позы,
        /// видимая площадь смены позы, рост силуэта над плечом и удержание
        /// параметром поднятого значения.
        /// </summary>
        private void Analyze(
            CubismModelNode model, Image lowered, Image stability, Image raised)
        {
            var bg = lowered.GetPixel(2, 2);
            Pass($"background reference color: {bg}");

            var sil = SilhouetteBounds(lowered, bg);
            if (sil.Size.X <= 0 || sil.Size.Y <= 0)
            {
                Fail("avatar silhouette not found on the lowered frame");
                return;
            }
            Pass($"silhouette bbox: pos={sil.Position} size={sil.Size}");

            int framePixels = lowered.GetWidth() * lowered.GetHeight();

            // 1) Детерминизм: статичная поза должна рендериться идентично.
            int stabilityDiff = CountChangedPixels(lowered, stability);
            double stabilityRatio = stabilityDiff / (double)framePixels;
            if (stabilityRatio <= 0.001)
                Pass($"static pose deterministic: {stabilityDiff} changed px ({stabilityRatio:P3})");
            else
                Fail($"static pose not deterministic: {stabilityDiff} changed px ({stabilityRatio:P3}) — ambient animation is on");

            // 2) Команда смены позы должна перекрасить значимую часть кадра.
            int poseDiff = CountChangedPixels(lowered, raised);
            double poseRatio = poseDiff / (double)framePixels;
            if (poseRatio >= 0.003)
                Pass($"pose change visible: {poseDiff} changed px ({poseRatio:P3})");
            else
                Fail($"pose change too small: {poseDiff} changed px ({poseRatio:P3})");

            // 3) CV: поднятая рука добавляет силуэт ЗА пределами опущенного
            // bbox'а на высоте плеча (лево или право — модель может быть
            // зеркальна зрителю). Полосы фиксированы по bbox опущенной позы,
            // поэтому не съезжают вместе с bbox поднятой руки.
            int stripH = System.Math.Max(1, (int)(sil.Size.Y * 0.30f));
            int stripY = (int)(sil.Position.Y + sil.Size.Y * 0.25f);
            int stripW = System.Math.Max(1, (int)(sil.Size.X * 2.0f));
            int margin = 4;
            int leftX = System.Math.Max(0, (int)sil.Position.X - margin - stripW);
            int rightX = System.Math.Min(lowered.GetWidth() - stripW, (int)sil.End.X + margin);
            var leftStrip = new Rect2I(leftX, stripY, stripW, stripH);
            var rightStrip = new Rect2I(rightX, stripY, stripW, stripH);
            int loweredLeft = CountModelPixels(lowered, leftStrip, bg);
            int raisedLeft = CountModelPixels(raised, leftStrip, bg);
            int loweredRight = CountModelPixels(lowered, rightStrip, bg);
            int raisedRight = CountModelPixels(raised, rightStrip, bg);
            int gain = System.Math.Max(raisedLeft - loweredLeft, raisedRight - loweredRight);
            int threshold = System.Math.Max(300, (int)(stripW * stripH * 0.02f));
            GameLogger.Log("TEST",
                $"  side strips @shoulder: left {loweredLeft}->{raisedLeft}, right {loweredRight}->{raisedRight} px (strip {stripW}x{stripH}, need {threshold})");
            if (gain >= threshold)
                Pass($"raised arm silhouette appears beside the body: +{gain} px");
            else
                Fail($"no raised-arm silhouette beside the body: +{gain} px (threshold {threshold})");
            // 4) Параметр держит поднятое значение между кадрами
            // (регрессия на Load/SaveParameters-брекет и SetLiveParameter).
            var armNow = model.FindParameter(RightArmParam);
            if (armNow != null && System.Math.Abs(armNow.Value - armNow.MaximumValue) < 0.001f)
                Pass($"{RightArmParam} holds MaximumValue across frames ({armNow.Value})");
            else
                Fail($"{RightArmParam} did not hold MaximumValue (value={armNow?.Value})");
        }

        private async System.Threading.Tasks.Task Delay(float seconds) =>
            await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

        /// <summary>Ждёт обработки кадра и снимает вьюпорт в RGBA8.</summary>
        private async System.Threading.Tasks.Task<Image> Capture()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var image = GetViewport().GetTexture().GetImage();
            image.Convert(Image.Format.Rgba8);
            return image;
        }

        private void SavePng(string fileName, Image image)
        {
            var path = Path.Combine(artifactDir, fileName);
            if (image.SavePng(path) == Error.Ok)
                Pass($"screenshot saved: {path}");
            else
                Fail($"SavePng failed for '{path}'");
        }

        private static Rect2 SilhouetteBounds(Image image, Color bg)
        {
            byte[] data = image.GetData();
            int w = image.GetWidth(), h = image.GetHeight();
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (IsFarFromBackground(data, i, bg))
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX < 0 ? new Rect2(0, 0, 0, 0) : new Rect2(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private static int CountModelPixels(Image image, Rect2I region, Color bg)
        {
            byte[] data = image.GetData();
            int w = image.GetWidth();
            int count = 0;
            for (int y = region.Position.Y; y < region.End.Y; y++)
            {
                for (int x = region.Position.X; x < region.End.X; x++)
                {
                    if (IsFarFromBackground(data, (y * w + x) * 4, bg)) count++;
                }
            }
            return count;
        }

        private static int CountChangedPixels(Image a, Image b)
        {
            byte[] da = a.GetData(), db = b.GetData();
            int count = 0;
            int limit = System.Math.Min(da.Length, db.Length);
            for (int i = 0; i < limit; i += 4)
            {
                if (System.Math.Abs(da[i] - db[i]) > DiffTolerance * 255f
                    || System.Math.Abs(da[i + 1] - db[i + 1]) > DiffTolerance * 255f
                    || System.Math.Abs(da[i + 2] - db[i + 2]) > DiffTolerance * 255f)
                    count++;
            }
            return count;
        }

        private static bool IsFarFromBackground(byte[] data, int i, Color bg) =>
            System.Math.Abs(data[i] / 255f - bg.R) > SilhouetteTolerance
            || System.Math.Abs(data[i + 1] / 255f - bg.G) > SilhouetteTolerance
            || System.Math.Abs(data[i + 2] / 255f - bg.B) > SilhouetteTolerance;

        private void Pass(string msg)
        {
            passed++;
            GameLogger.Log("TEST", $"  [PASS] {msg}");
        }

        private void Fail(string msg)
        {
            failed++;
            GameLogger.Log("TEST", $"  [FAIL] {msg}", LogLevel.Error);
        }
    }
}
