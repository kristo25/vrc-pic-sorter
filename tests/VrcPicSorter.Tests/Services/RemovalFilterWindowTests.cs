using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SixLabors.ImageSharp;
using VrcPicSorter.App;
using VrcPicSorter.App.Services;
using VrcPicSorter.Core.Imaging;
using VrcPicSorter.Core.Models;
using VrcPicSorter.Tests.Imaging;

namespace VrcPicSorter.Tests.Services;

public static class RemovalFilterWindowTests
{
    public static async Task VerifyFilterReviewAsync()
    {
        MainWindow? window = null;
        try
        {
            using var directory = new TestDirectory();
            using var runtime = new AppRuntime(directory.GetPath("state"), allowStartupRegistration: false);
            using var image = ImageFixtureFactory.CreatePattern(23);
            var path = directory.GetPath("archive.png");
            await image.SaveAsPngAsync(path);
            var fingerprint = ImageFingerprint.Create(ImageFixtureFactory.ToDecodedImage(image));
            var review = new ReviewItem
            {
                Id = Guid.NewGuid(),
                Category = VrcImageCategory.Prints,
                IncomingOriginalPath = path,
                HeldFilePath = path,
                IncomingFingerprint = fingerprint.ExactIdentity,
                IncomingImageFingerprint = fingerprint,
                FilterReview = new() { IsArchive = true, Reasons = [RemovalFilterReason.PrintFrame] }
            };
            await runtime.StateStore.UpdateAsync(s => { s.ReviewQueue.Add(review); return true; });
            window = new MainWindow(runtime) { Width = 920, Height = 640 };
            T Control<T>(string name) => (T)window.FindName(name);
            var queue = Control<ListBox>("QueueList");
            queue.ItemsSource = new[] { review }; queue.SelectedItem = review;
            await (Task)Invoke(window, "LoadGroupReviewAsync")!;
            Assert.Equal(Visibility.Visible, Control<WrapPanel>("FilterReviewActions").Visibility);
            Assert.Equal(Visibility.Collapsed, Control<WrapPanel>("NormalReviewActions").Visibility);
            Assert.Equal(Visibility.Collapsed, Control<Grid>("ArchiveColumn").Visibility);
            Assert.Equal("Archive", Control<TextBlock>("IncomingOriginLabel").Text);
            Assert.Contains("Remove Print Frame", Control<TextBlock>("CandidateHint").Text);
            Assert.True(Control<Button>("KeepFilterButton").IsEnabled);
            Assert.True(Control<Button>("RemoveFilterButton").IsEnabled);
            Assert.False(Control<Button>("ClearQueueButton").IsEnabled);
            typeof(MainWindow).GetField("_groupLoading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Invoke(window, "UpdateGroupAvailability");
            Assert.False(Control<Button>("RemoveFilterButton").IsEnabled);
            Control<TextBox>("PlayerNameField").Text = "Exact_Name";
            Assert.True(Control<CheckBox>("RemoveMyImagesCheck").IsEnabled);
            Control<TextBox>("PlayerNameField").Clear();
            Assert.False(Control<CheckBox>("RemoveMyImagesCheck").IsEnabled);
            System.IO.File.Delete(path);
            await (Task)Invoke(window, "LoadGroupReviewAsync")!;
            Assert.False(Control<Button>("KeepFilterButton").IsEnabled);
            Assert.False(Control<Button>("RemoveFilterButton").IsEnabled);
            Assert.True(Control<Button>("DismissFilterReviewButton").IsEnabled);
            Assert.True(Control<Button>("ClearQueueButton").IsEnabled);
            Assert.Contains("confirmed absent", Control<TextBlock>("IncomingAvailabilityText").Text);
            await System.IO.File.WriteAllTextAsync(path, "changed image");
            await (Task)Invoke(window, "LoadGroupReviewAsync")!;
            Assert.False(Control<Button>("DismissFilterReviewButton").IsEnabled);
            Assert.False(Control<Button>("ClearQueueButton").IsEnabled);
            review.FilterReview = null;
            await (Task)Invoke(window, "LoadGroupReviewAsync")!;
            Assert.Equal(Visibility.Visible, Control<WrapPanel>("NormalReviewActions").Visibility);
            Assert.Equal(Visibility.Collapsed, Control<WrapPanel>("FilterReviewActions").Visibility);
            Assert.Equal("Keep incoming only", Control<Button>("ApplyGroupButton").Content);
        }
        finally { window?.Close(); }
    }

    private static object? Invoke(MainWindow window, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
}

