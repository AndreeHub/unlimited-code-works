import os

path = r"c:\Users\lic\Desktop\3 SWD\ReviewScope\src\ReviewScope.App\MainWindow.xaml"
if not os.path.exists(path):
    print(f"ERROR: File not found at {path}")
    exit(1)

with open(path, "r", encoding="utf-8") as f:
    content = f.read()

target = """                                                <TextBlock Text="Text Size" Foreground="{StaticResource SubtleForeground}" FontSize="11" FontWeight="SemiBold" Margin="0,0,0,4"/>
                                                <StackPanel Orientation="Horizontal">
                                                    <TextBox Text="{Binding SelectedFontSize, UpdateSourceTrigger=PropertyChanged}" Width="56" FontSize="11" Padding="4,3" Margin="0,0,6,0" KeyDown="OnApplySelectionPropertiesKeyDown"/>
                                                    <Button Style="{StaticResource ToolButton}" Content="Apply" Click="OnApplySelectionProperties" Height="26"/>
                                                </StackPanel>"""

replacement = """                                                <TextBlock Text="Text Size" Foreground="{StaticResource SubtleForeground}" FontSize="11" FontWeight="SemiBold" Margin="0,0,0,4"/>
                                                <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                                                    <TextBox Text="{Binding SelectedFontSize, UpdateSourceTrigger=PropertyChanged}" Width="56" FontSize="11" Padding="4,3" Margin="0,0,6,0" KeyDown="OnApplySelectionPropertiesKeyDown"/>
                                                    <Button Style="{StaticResource ToolButton}" Content="Apply" Click="OnApplySelectionProperties" Height="26"/>
                                                </StackPanel>

                                                <TextBlock Text="Opacity" Foreground="{StaticResource SubtleForeground}" FontSize="11" FontWeight="SemiBold" Margin="0,4,0,4"/>
                                                <DockPanel Margin="0,0,0,8">
                                                    <TextBlock Text="{Binding SelectedOpacity, StringFormat={}{0:P0}}" DockPanel.Dock="Right" FontSize="11" Foreground="{StaticResource SubtleForeground}" Margin="8,0,0,0" VerticalAlignment="Center" Width="38" TextAlignment="Right"/>
                                                    <Slider Minimum="0" Maximum="1" Value="{Binding SelectedOpacity, Mode=TwoWay}" SmallChange="0.05" LargeChange="0.1" TickFrequency="0.05" IsSnapToTickEnabled="True" VerticalAlignment="Center"/>
                                                </DockPanel>

                                                <TextBlock Text="Corner Rounding" Foreground="{StaticResource SubtleForeground}" FontSize="11" FontWeight="SemiBold" Margin="0,4,0,4"/>
                                                <UniformGrid Columns="4" Margin="0,0,0,4">
                                                    <Button Style="{StaticResource ToolButton}" Content="Sharp" Click="OnSelectCornerRadius" CommandParameter="0" Margin="0,0,4,0"/>
                                                    <Button Style="{StaticResource ToolButton}" Content="Sm" Click="OnSelectCornerRadius" CommandParameter="4" Margin="0,0,4,0"/>
                                                    <Button Style="{StaticResource ToolButton}" Content="Md" Click="OnSelectCornerRadius" CommandParameter="8" Margin="0,0,4,0"/>
                                                    <Button Style="{StaticResource ToolButton}" Content="Lg" Click="OnSelectCornerRadius" CommandParameter="16"/>
                                                </UniformGrid>"""

# Replace target normalizing line endings
content_norm = content.replace("\r\n", "\n")
target_norm = target.replace("\r\n", "\n")
replacement_norm = replacement.replace("\r\n", "\n")

if target_norm in content_norm:
    content_norm = content_norm.replace(target_norm, replacement_norm)
    # Restore CRLF line endings
    content = content_norm.replace("\n", "\r\n")
    with open(path, "w", encoding="utf-8") as f:
        f.write(content)
    print("SUCCESS")
else:
    print("FAILED TO FIND TARGET")
