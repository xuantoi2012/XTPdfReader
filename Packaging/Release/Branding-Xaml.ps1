function Get-AmbientMarkup([string]$RootDirectory) {
    $markup = [IO.File]::ReadAllText((Join-Path $RootDirectory 'Controls\AmbientBackdrop.xaml'))
    $markup = $markup -replace 'x:Class="[^"]*"', 'Grid.RowSpan="3"'
    return $markup
}

function Set-AmbientPhase($Surface, [double]$Phase) {
    $angle = 2 * [Math]::PI * $Phase
    $Surface.FindName('LavenderLight').RenderTransform.X = 14 * [Math]::Sin($angle)
    $Surface.FindName('LavenderLight').RenderTransform.Y = 8 * [Math]::Cos($angle)
    $Surface.FindName('BlueLight').RenderTransform.X = -16 * [Math]::Sin($angle)
    $Surface.FindName('BlueLight').RenderTransform.Y = -10 * [Math]::Cos($angle)
    $Surface.FindName('MintLight').RenderTransform.X = 10 * [Math]::Cos($angle)
    $Surface.FindName('MintLight').RenderTransform.Y = -8 * [Math]::Sin($angle)
}
