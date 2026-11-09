using System;
using System.Collections.Generic;

namespace PcgCitySandbox.Ui;

/// <summary>
/// A value several controls can show at once (e.g. a layer toggle on the stage page and on the view page):
/// every bound control follows changes made anywhere.
/// </summary>
public sealed class Setting<T>
{
    private T _value;

    public Setting(T value) => _value = value;

    public event Action<T>? Changed;

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value))
                return;
            _value = value;
            Changed?.Invoke(value);
        }
    }

    public static implicit operator T(Setting<T> setting) => setting.Value;
}
