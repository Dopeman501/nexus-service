using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.KeyReactive;

public sealed partial class K55CoreReactionLayout
{
    // Factual physical key membership, projected from the hashed shipped CORE manifest.
    // Fn/International names remain explicit; a key source must actually emit them to react.
    private static readonly IReadOnlyDictionary<string, Dictionary<string, int>> KeyZones = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
    {
        ["US"] = new(StringComparer.Ordinal)
        {
            ["`"] = 0, ["Escape"] = 0, ["1"] = 0, ["Tab"] = 0, ["Caps Lock"] = 0, ["Left Shift"] = 0, ["Left Control"] = 0,
            ["F1"] = 1, ["F2"] = 1, ["2"] = 1, ["3"] = 1, ["W"] = 1, ["Q"] = 1, ["S"] = 1, ["A"] = 1, ["Z"] = 1, ["X"] = 1, ["Left Windows"] = 1, ["Left Alt"] = 1,
            ["F3"] = 2, ["F4"] = 2, ["4"] = 2, ["5"] = 2, ["T"] = 2, ["R"] = 2, ["E"] = 2, ["D"] = 2, ["G"] = 2, ["F"] = 2, ["C"] = 2, ["V"] = 2,
            ["F6"] = 3, ["F5"] = 3, ["6"] = 3, ["7"] = 3, ["U"] = 3, ["Y"] = 3, ["H"] = 3, ["J"] = 3, ["N"] = 3, ["B"] = 3, ["Space"] = 3,
            ["F8"] = 4, ["F7"] = 4, ["0"] = 4, ["9"] = 4, ["8"] = 4, ["O"] = 4, ["I"] = 4, ["L"] = 4, ["K"] = 4, [","] = 4, ["M"] = 4,
            ["F10"] = 5, ["F9"] = 5, ["="] = 5, ["-"] = 5, ["P"] = 5, ["["] = 5, [";"] = 5, ["'"] = 5, ["."] = 5, ["/"] = 5, ["Right Alt"] = 5, ["Fn"] = 5,
            ["F12"] = 6, ["F11"] = 6, ["]"] = 6, ["Enter"] = 6, ["Right Shift"] = 6, ["\\"] = 6, ["Backspace"] = 6, ["Menu"] = 6, ["Right Control"] = 6,
            ["Print Screen"] = 7, ["Scroll Lock"] = 7, ["Pause/Break"] = 7, ["Page Up"] = 7, ["Insert"] = 7, ["Home"] = 7, ["Page Down"] = 7, ["Delete"] = 7, ["End"] = 7, ["Up Arrow"] = 7, ["Left Arrow"] = 7, ["Down Arrow"] = 7, ["Right Arrow"] = 7,
            ["Num Lock"] = 8, ["Number Pad /"] = 8, ["Number Pad 7"] = 8, ["Number Pad 8"] = 8, ["Number Pad 4"] = 8, ["Number Pad 5"] = 8, ["Number Pad 1"] = 8, ["Number Pad 2"] = 8, ["Number Pad 0"] = 8,
            ["Number Pad *"] = 9, ["Number Pad -"] = 9, ["Number Pad 9"] = 9, ["Number Pad 6"] = 9, ["Number Pad 3"] = 9, ["Number Pad ."] = 9, ["Number Pad +"] = 9, ["Number Pad Enter"] = 9,
        },
        ["UK"] = new(StringComparer.Ordinal)
        {
            ["`"] = 0, ["Escape"] = 0, ["\\ (ISO)"] = 0, ["1"] = 0, ["Tab"] = 0, ["Caps Lock"] = 0, ["Left Shift"] = 0, ["Left Control"] = 0,
            ["F1"] = 1, ["F2"] = 1, ["2"] = 1, ["3"] = 1, ["W"] = 1, ["Q"] = 1, ["S"] = 1, ["A"] = 1, ["Z"] = 1, ["X"] = 1, ["Left Windows"] = 1, ["Left Alt"] = 1,
            ["F3"] = 2, ["F4"] = 2, ["4"] = 2, ["5"] = 2, ["T"] = 2, ["R"] = 2, ["E"] = 2, ["D"] = 2, ["G"] = 2, ["F"] = 2, ["C"] = 2, ["V"] = 2,
            ["F6"] = 3, ["F5"] = 3, ["6"] = 3, ["7"] = 3, ["U"] = 3, ["Y"] = 3, ["H"] = 3, ["J"] = 3, ["N"] = 3, ["B"] = 3, ["Space"] = 3,
            ["F8"] = 4, ["F7"] = 4, ["0"] = 4, ["9"] = 4, ["8"] = 4, ["O"] = 4, ["I"] = 4, ["L"] = 4, ["K"] = 4, [","] = 4, ["M"] = 4,
            ["F10"] = 5, ["F9"] = 5, ["="] = 5, ["-"] = 5, ["P"] = 5, ["["] = 5, [";"] = 5, ["'"] = 5, ["."] = 5, ["/"] = 5, ["Right Alt"] = 5, ["Fn"] = 5,
            ["F12"] = 6, ["F11"] = 6, ["]"] = 6, ["\\"] = 6, ["Right Shift"] = 6, ["Enter"] = 6, ["Backspace"] = 6, ["Menu"] = 6, ["Right Control"] = 6,
            ["Print Screen"] = 7, ["Scroll Lock"] = 7, ["Pause/Break"] = 7, ["Page Up"] = 7, ["Insert"] = 7, ["Home"] = 7, ["Page Down"] = 7, ["Delete"] = 7, ["End"] = 7, ["Up Arrow"] = 7, ["Left Arrow"] = 7, ["Down Arrow"] = 7, ["Right Arrow"] = 7,
            ["Num Lock"] = 8, ["Number Pad /"] = 8, ["Number Pad 7"] = 8, ["Number Pad 8"] = 8, ["Number Pad 4"] = 8, ["Number Pad 5"] = 8, ["Number Pad 1"] = 8, ["Number Pad 2"] = 8, ["Number Pad 0"] = 8,
            ["Number Pad *"] = 9, ["Number Pad -"] = 9, ["Number Pad 9"] = 9, ["Number Pad 6"] = 9, ["Number Pad 3"] = 9, ["Number Pad ."] = 9, ["Number Pad +"] = 9, ["Number Pad Enter"] = 9,
        },
        ["BR"] = new(StringComparer.Ordinal)
        {
            ["`"] = 0, ["Escape"] = 0, ["1"] = 0, ["Tab"] = 0, ["Caps Lock"] = 0, ["Left Shift"] = 0, ["Left Control"] = 0, ["\\ (ISO)"] = 0,
            ["F1"] = 1, ["F2"] = 1, ["2"] = 1, ["3"] = 1, ["W"] = 1, ["Q"] = 1, ["S"] = 1, ["A"] = 1, ["Z"] = 1, ["X"] = 1, ["Left Windows"] = 1, ["Left Alt"] = 1,
            ["F3"] = 2, ["F4"] = 2, ["4"] = 2, ["5"] = 2, ["T"] = 2, ["R"] = 2, ["E"] = 2, ["D"] = 2, ["G"] = 2, ["F"] = 2, ["C"] = 2, ["V"] = 2,
            ["F6"] = 3, ["F5"] = 3, ["6"] = 3, ["7"] = 3, ["U"] = 3, ["Y"] = 3, ["H"] = 3, ["J"] = 3, ["N"] = 3, ["B"] = 3, ["Space"] = 3,
            ["F8"] = 4, ["F7"] = 4, ["0"] = 4, ["9"] = 4, ["8"] = 4, ["O"] = 4, ["I"] = 4, ["L"] = 4, ["K"] = 4, [","] = 4, ["M"] = 4,
            ["F10"] = 5, ["F9"] = 5, ["="] = 5, ["-"] = 5, ["P"] = 5, ["["] = 5, [";"] = 5, ["'"] = 5, ["."] = 5, ["/"] = 5, ["Right Alt"] = 5, ["Fn"] = 5,
            ["F12"] = 6, ["F11"] = 6, ["]"] = 6, ["\\"] = 6, ["Right Shift"] = 6, ["Enter"] = 6, ["Backspace"] = 6, ["Menu"] = 6, ["Right Control"] = 6, ["International1"] = 6,
            ["Print Screen"] = 7, ["Scroll Lock"] = 7, ["Pause/Break"] = 7, ["Page Up"] = 7, ["Insert"] = 7, ["Home"] = 7, ["Page Down"] = 7, ["Delete"] = 7, ["End"] = 7, ["Up Arrow"] = 7, ["Left Arrow"] = 7, ["Down Arrow"] = 7, ["Right Arrow"] = 7,
            ["Num Lock"] = 8, ["Number Pad /"] = 8, ["Number Pad 7"] = 8, ["Number Pad 8"] = 8, ["Number Pad 4"] = 8, ["Number Pad 5"] = 8, ["Number Pad 1"] = 8, ["Number Pad 2"] = 8, ["Number Pad 0"] = 8,
            ["Number Pad *"] = 9, ["Number Pad -"] = 9, ["Number Pad 9"] = 9, ["Number Pad 6"] = 9, ["Number Pad 3"] = 9, ["Number Pad ."] = 9, ["Number Pad +"] = 9, ["Number Pad Enter"] = 9,
        },
        ["JP"] = new(StringComparer.Ordinal)
        {
            ["`"] = 0, ["Escape"] = 0, ["1"] = 0, ["Tab"] = 0, ["Caps Lock"] = 0, ["Left Shift"] = 0, ["Left Control"] = 0,
            ["F1"] = 1, ["F2"] = 1, ["2"] = 1, ["3"] = 1, ["W"] = 1, ["Q"] = 1, ["S"] = 1, ["A"] = 1, ["Z"] = 1, ["X"] = 1, ["Left Windows"] = 1, ["Left Alt"] = 1,
            ["F3"] = 2, ["F4"] = 2, ["4"] = 2, ["5"] = 2, ["T"] = 2, ["R"] = 2, ["E"] = 2, ["D"] = 2, ["G"] = 2, ["F"] = 2, ["C"] = 2, ["V"] = 2, ["International5"] = 2,
            ["F6"] = 3, ["F5"] = 3, ["6"] = 3, ["7"] = 3, ["U"] = 3, ["Y"] = 3, ["H"] = 3, ["J"] = 3, ["N"] = 3, ["B"] = 3, ["Space"] = 3,
            ["F8"] = 4, ["F7"] = 4, ["0"] = 4, ["9"] = 4, ["8"] = 4, ["O"] = 4, ["I"] = 4, ["L"] = 4, ["K"] = 4, [","] = 4, ["M"] = 4, ["International4"] = 4,
            ["F10"] = 5, ["F9"] = 5, ["="] = 5, ["-"] = 5, ["P"] = 5, ["["] = 5, [";"] = 5, ["'"] = 5, ["."] = 5, ["/"] = 5, ["International2"] = 5, ["Fn"] = 5,
            ["F12"] = 6, ["F11"] = 6, ["]"] = 6, ["\\"] = 6, ["Right Shift"] = 6, ["Enter"] = 6, ["Backspace"] = 6, ["Menu"] = 6, ["Right Control"] = 6, ["International1"] = 6, ["International3"] = 6,
            ["Print Screen"] = 7, ["Scroll Lock"] = 7, ["Pause/Break"] = 7, ["Page Up"] = 7, ["Insert"] = 7, ["Home"] = 7, ["Page Down"] = 7, ["Delete"] = 7, ["End"] = 7, ["Up Arrow"] = 7, ["Left Arrow"] = 7, ["Down Arrow"] = 7, ["Right Arrow"] = 7,
            ["Num Lock"] = 8, ["Number Pad /"] = 8, ["Number Pad 7"] = 8, ["Number Pad 8"] = 8, ["Number Pad 4"] = 8, ["Number Pad 5"] = 8, ["Number Pad 1"] = 8, ["Number Pad 2"] = 8, ["Number Pad 0"] = 8,
            ["Number Pad *"] = 9, ["Number Pad -"] = 9, ["Number Pad 9"] = 9, ["Number Pad 6"] = 9, ["Number Pad 3"] = 9, ["Number Pad ."] = 9, ["Number Pad +"] = 9, ["Number Pad Enter"] = 9,
        },
    };
}
