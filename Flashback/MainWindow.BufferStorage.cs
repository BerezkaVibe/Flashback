using System;
using System.Windows;
using System.Windows.Controls;

namespace Flashback;

// Settings > Performance: whether the replay buffer is kept on disk or in memory (see MemoryBuffer).
public partial class MainWindow
{
    private void BufferStorage_Changed(object sender, SelectionChangedEventArgs e) => UpdateBufferStorageNote();

    // What the choice means at the current settings: the buffer's size against what memory may hold, and what
    // the running buffer is doing.
    private void UpdateBufferStorageNote()
    {
        if (BufferStorageNote == null || BufferStorageBox == null || settings == null) return;
        bool memoryChosen = BufferStorageBox.SelectedIndex == 1;
        double estimate = settings.EstimatedBufferMb, limit = MemoryBuffer.LimitMb;
        string text = $"At the applied settings the buffer holds about {estimate:0} MB. Memory may hold up to {limit:0} MB of buffer on this PC.";
        if (memoryChosen && estimate > limit) text += " That is more than it allows, so the buffer stays on disk; shorten the replay or lower the quality to use memory.";
        else if (memoryChosen && recorder != null && recorder.IsRecording && !recorder.BufferInMemory) text += " The buffer restarts in memory when you apply this.";
        if (recorder != null && recorder.IsRecording) text += recorder.BufferInMemory ? $" Right now it holds {recorder.BufferBytes / 1048576.0:0} MB in memory." : " Right now it is on disk.";
        BufferStorageNote.Text = text;
    }
}
