using System.Collections.Concurrent;

namespace Genesis;

/// <summary>
/// Реализация алгоритма дробного индексирования (fractional indexing).
/// Позволяет вставлять элементы между другими без пересчета всех индексов.
/// Основано на https://github.com/rocicorp/fractional-indexing
/// </summary>
public static class FractionalIndexing
{
    /// <summary>
    /// Алфавит Base62: цифры, заглавные латинские буквы, строчные латинские буквы
    /// </summary>
    public const string Base62Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Алфавит Base52: заглавные и строчные латинские буквы (без цифр)
    /// </summary>
    public const string Base52Digits = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Кэш для быстрого поиска индекса символа в алфавите
    /// </summary>
    private static readonly ConcurrentDictionary<string, int[]> DigitIndexCache = new();

    /// <summary>
    /// Двухуровневый кэш для проверки "наименьшего целого"
    /// </summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, string>> RepeatedKeysCache = new();

    /// <summary>
    /// Множество уже провалидированных алфавитов цифр
    /// </summary>
    private static readonly HashSet<string> ValidatedDigits = [];

    /// <summary>
    /// Множество уже провалидированных алфавитов головок
    /// </summary>
    private static readonly HashSet<string> ValidatedIntDigits = [];

    /// <summary>
    /// Получает массив индексов для указанного алфавита цифр.
    /// Массив размером 256, где индекс равен коду символа, а значение — его позиция в алфавите.
    /// Символы вне алфавита имеют значение 0 (так как массив инициализируется нулями, 
    /// и валидация гарантирует, что входные данные корректны).
    /// </summary>
    /// <param name="digits"> Строка алфавита цифр </param>
    /// <returns> Массив целых чисел размером 256, отображающий код символа на его индекс в алфавите </returns>
    private static int[] GetDigitIndex(string digits)
    {
        return DigitIndexCache.GetOrAdd(digits, d =>
        {
            var lookup = new int[256];

            for (int i = 0; i < d.Length; i++)
            {
                char c = d[i];
                if (c > 255)
                {
                    throw new ArgumentException($"Символ '{c}' в алфавите превышает диапазон 255.", nameof(digits));
                }
                lookup[c] = i;
            }
            return lookup;
        });
    }

    /// <summary>
    /// Вычисляет середину между двумя строками `a` и `b` в данном алфавите.
    /// Результат гарантированно лексикографически больше `a` и меньше `b` (если `b` не null).
    /// Не допускаются завершающие нули.
    /// </summary>
    /// <param name="a"> Нижняя граница. Может быть пустой строкой ("") </param>
    /// <param name="b"> Верхняя граница. Может быть null (означает бесконечность) </param>
    /// <param name="digits"> Алфавит цифр </param>
    /// <param name="lookup"> Предвычисленный массив индексов для алфавита <paramref name="digits"/> </param>
    /// <returns> Строка, представляющая собой промежуточный ключ </returns>
    private static string Midpoint(string a, string? b, string digits, int[] lookup)
    {
        char zero = digits[0];

        if (b != null && string.CompareOrdinal(a, b) >= 0)
        {
            throw new ArgumentException($"{a} >= {b}");
        }

        if (a.EndsWith(zero) || (b != null && b.EndsWith(zero)))
        {
            throw new ArgumentException("Недопустимый завершающий ноль.");
        }

        if (b != null)
        {
            // Удаляем самый длинный общий префикс.
            // Дополняем `a` нулями по мере продвижения.
            // Нам не нужно дополнять `b`, так как он не может закончиться раньше `a` при прохождении общего префикса.
            int n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : zero) == b[n])
            {
                n++;
            }

            if (n > 0)
            {
                // Безопасное извлечение подстрок.
                // В JS slice(n) работает даже если n > length (возвращает ""),
                // в C# range operator [n..] кидает исключение ArgumentOutOfRangeException.
                string remainingA = n < a.Length ? a[n..] : "";
                string remainingB = n < b.Length ? b[n..] : "";

                return b[..n] + Midpoint(remainingA, remainingB, digits, lookup);
            }
        }

        // Первые цифры (или их отсутствие) отличаются.

        // Если a пусто, digitA = 0
        int digitA = a.Length > 0 ? lookup[a[0]] : 0;

        // Если b null, digitB = длина алфавита
        int digitB = b != null ? lookup[b[0]] : digits.Length;

        if (digitB - digitA > 1)
        {
            // Используем AwayFromZero для соответствия Math.round() в JS
            int midDigit = (int)Math.Round(0.5 * (digitA + digitB), MidpointRounding.AwayFromZero);
            return digits[midDigit].ToString();
        }
        else
        {
            // Первые цифры последовательны.
            if (b != null && b.Length > 1)
            {
                return b[..1];
            }
            else
            {
                // `b` равно null или имеет длину 1 (одна цифра).
                // Первая цифра `a` является предыдущей относительно `b`,
                // или последней, если `b` равно null.
                // Пример: midpoint('49', '5') -> '4' + midpoint('9', null) -> '4' + '9' + midpoint('', null) -> '495'.

                // Безопасное извлечение остатка a
                string remainingA = a.Length > 1 ? a[1..] : "";

                return digits[digitA] + Midpoint(remainingA, null, digits, lookup);
            }
        }
    }

    /// <summary>
    /// Проверяет корректность целочисленной части ключа.
    /// Длина части должна соответствовать ожидаемой длине, определяемой первым символом (головкой).
    /// </summary>
    /// <param name="intPart"> Целочисленная часть ключа </param>
    /// <param name="intDigits"> Алфавит головок (маркеров длины) </param>
    /// <param name="intLookup"> Массив индексов для алфавита <paramref name="intDigits"/></param>
    private static void ValidateInteger(string intPart, string intDigits, int[] intLookup)
    {
        if (intPart.Length != GetIntegerLength(intPart[0], intDigits, intLookup))
        {
            throw new ArgumentException($"Недопустимая целочисленная часть ключа порядка: {intPart}");
        }
    }

    /// <summary>
    /// Определяет ожидаемую длину целочисленной части по её первому символу (головке).
    /// Головки расположены симметрично: первая половина — отрицательные длины, вторая — положительные.
    /// </summary>
    /// <param name="head"> Первый символ целочисленной части (головка) </param>
    /// <param name="intDigits"> Алфавит головок </param>
    /// <param name="intLookup"> Массив индексов для алфавита <paramref name="intDigits"/></param>
    /// <returns> Ожидаемая общая длина целочисленной части (включая головку) </returns>
    private static int GetIntegerLength(char head, string intDigits, int[] intLookup)
    {
        if (head > 255) throw new ArgumentException("Головка должна быть однобайтовым символом.");

        int i = intLookup[head];

        // Проверка, действительно ли символ находится в этом индексе,
        // так как lookup возвращает 0 для любых символов вне алфавита.
        if (i < intDigits.Length && intDigits[i] == head)
        {
            int half = intDigits.Length / 2;
            // Формула расчета длины зависит от того, находимся ли мы в левой (отрицательной) или правой (положительной) половине
            return i < half ? half - i + 1 : i - half + 2;
        }

        throw new ArgumentException($"Недопустимая головка ключа порядка: {head}");
    }

    /// <summary>
    /// Извлекает целочисленную часть из полного ключа.
    /// Целочисленная часть всегда начинается с первой позиции и ее длина определяется первой буквой.
    /// </summary>
    /// <param name="key"> Полный ключ порядка </param>
    /// <param name="intDigits"> Алфавит головок </param>
    /// <param name="intLookup"> Массив индексов для алфавита <paramref name="intDigits"/></param>
    /// <returns> Подстрока, представляющая целочисленную часть </returns>
    private static string GetIntegerPart(string key, string intDigits, int[] intLookup)
    {
        int integerPartLength = GetIntegerLength(key[0], intDigits, intLookup);
        if (integerPartLength > key.Length)
        {
            throw new ArgumentException($"Недопустимый ключ порядка: {key}");
        }
        return key[..integerPartLength];
    }

    /// <summary>
    /// Полная валидация ключа порядка.
    /// Проверяет структуру головки, отсутствие завершающих нулей в дробной части и корректность целочисленной части.
    /// </summary>
    /// <param name="key"> Проверяемый ключ порядка </param>
    /// <param name="digits"> Алфавит цифр </param>
    /// <param name="intDigits"> Алфавит головок </param>
    /// <param name="intLookup"> Массив индексов для алфавита <paramref name="intDigits"/></param>
    private static void ValidateOrderKey(string key, string digits, string intDigits, int[] intLookup)
    {
        if (IsSmallestInteger(key, digits, intDigits))
        {
            throw new ArgumentException($"Недопустимый ключ порядка: {key}");
        }

        // GetIntegerPart выбросит исключение, если первый символ неверный или ключ слишком короткий.
        string i = GetIntegerPart(key, intDigits, intLookup);
        string f = key[i.Length..];

        if (f.EndsWith(digits[0]))
        {
            throw new ArgumentException($"Недопустимый ключ порядка (завершающий ноль в дробной части): {key}");
        }
    }

    /// <summary>
    /// Увеличивает целочисленную часть на единицу.
    /// Работает как сложение в столбик с учетом изменения длины (головки) при переходе через границы.
    /// </summary>
    /// <param name="x"> Текущая целочисленная часть </param>
    /// <param name="digits"> Алфавит цифр </param>
    /// <param name="lookup"> Массив индексов для <paramref name="digits"/></param>
    /// <param name="intDigits"> Алфавит головок </param>
    /// <param name="intLookup"> Массив индексов для <paramref name="intDigits"/></param>
    /// <returns> Новая увеличенная целочисленная часть, или null, если достигнуто максимальное значение </returns>
    private static string? IncrementInteger(string x, string digits, int[] lookup, string intDigits, int[] intLookup)
    {
        ValidateInteger(x, intDigits, intLookup);
        char head = x[0];
        char zero = digits[0];

        // Идем справа налево по цифровой части, превращая максимумы в нули (`trailing`), пока не найдем ту, которую можно увеличить.
        string trailing = "";
        for (int i = x.Length - 1; i >= 1; i--)
        {
            int d = lookup[x[i]] + 1;
            if (d == digits.Length)
            {
                trailing = zero + trailing;
            }
            else
            {
                return head + x[1..i] + digits[d] + trailing;
            }
        }

        // Перенос выходит за пределы всей цифровой части; `trailing` теперь состоит из одних нулей.
        int headIndex = intLookup[head];
        if (headIndex == intDigits.Length - 1)
        {
            // Уже наибольшее целое
            return null;
        }

        char h = intDigits[headIndex + 1];

        // Головка смещается на один шаг к наибольшей цифре; изменяем длину цифровой части, чтобы соответствовать новой длине головки.
        int lengthDelta = GetIntegerLength(h, intDigits, intLookup) - GetIntegerLength(head, intDigits, intLookup);

        if (lengthDelta > 0)
        {
            return h + trailing + zero;
        }
        else if (lengthDelta < 0)
        {
            // Отрезаем один символ из начала trailing, так как новая длина короче
            if (trailing.Length > 0)
                return h + trailing[1..];
            else
                return h.ToString();
        }
        else
        {
            return h + trailing;
        }
    }

    /// <summary>
    /// Уменьшает целочисленную часть на единицу.
    /// Работает как вычитание в столбик с учетом изменения длины (головки) при переходе через границы.
    /// </summary>
    /// <param name="x"> Текущая целочисленная часть </param>
    /// <param name="digits"> Алфавит цифр </param>
    /// <param name="lookup"> Массив индексов для <paramref name="digits"/></param>
    /// <param name="intDigits"> Алфавит головок </param>
    /// <param name="intLookup"> Массив индексов для <paramref name="intDigits"/></param>
    /// <returns> Новая уменьшенная целочисленная часть, или null, если достигнуто минимальное значение </returns>
    private static string? DecrementInteger(string x, string digits, int[] lookup, string intDigits, int[] intLookup)
    {
        ValidateInteger(x, intDigits, intLookup);
        char head = x[0];
        char last = digits[^1];

        // Идем справа налево по цифровой части, превращая минимумы в максимумы (`trailing`), пока не найдем ту, которую можно уменьшить.
        string trailing = "";
        for (int i = x.Length - 1; i >= 1; i--)
        {
            int d = lookup[x[i]] - 1;
            if (d == -1)
            {
                trailing = last + trailing;
            }
            else
            {
                return head + x[1..i] + digits[d] + trailing;
            }
        }

        // Заем выходит за пределы всей цифровой части; `trailing` теперь состоит из одних максимальных цифр.
        int headIndex = intLookup[head];
        if (headIndex == 0)
        {
            // Уже наименьшее целое
            return null;
        }

        char h = intDigits[headIndex - 1];

        // Головка смещается на один шаг к наименьшей цифре; изменяем длину цифровой части.
        int lengthDelta = GetIntegerLength(h, intDigits, intLookup) - GetIntegerLength(head, intDigits, intLookup);

        if (lengthDelta > 0)
        {
            return h + trailing + last;
        }
        else if (lengthDelta < 0)
        {
            if (trailing.Length > 0)
                return h + trailing[1..];
            else
                return h.ToString();
        }
        else
        {
            return h + trailing;
        }
    }

    /// <summary>
    /// Проверяет, является ли ключ наименьшим возможным целым числом в данном пространстве.
    /// Наименьшее целое состоит из самой "левой" головки и максимально возможного количества нулей.
    /// </summary>
    /// <param name="key"> Проверяемый ключ </param>
    /// <param name="digits"> Алфавит цифр </param>
    /// <param name="intDigits"> Алфавит головок </param>
    /// <returns> True, если ключ представляет собой наименьшее возможное целое число </returns>
    private static bool IsSmallestInteger(string key, string digits, string intDigits)
    {
        var byDigit = RepeatedKeysCache.GetOrAdd(intDigits, _ => new ConcurrentDictionary<int, string>());

        int zeroCode = digits[0];
        string cached = byDigit.GetOrAdd(zeroCode, zc =>
        {
            // Формируем эталонную строку: первая головка + повторяющаяся первая цифра (zero) количество раз равное половине длины intDigits
            // Это соответствует максимальной длине отрицательной части.
            return intDigits[0] + new string(digits[0], intDigits.Length / 2);
        });

        return key == cached;
    }

    /// <summary>
    /// Возвращает true, если каждый символ строки <paramref name="s"/> имеет строго больший код символа, чем предыдущий.
    /// Это обеспечивает правильную сортировку алфавита.
    /// </summary>
    /// <param name="s"> Проверяемая строка </param>
    /// <returns> True, если строка строго возрастающая по кодам символов; иначе false </returns>
    private static bool IsStrictlyAscending(string s)
    {
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i - 1] >= s[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Возвращает true, если каждый символ строки <paramref name="s"/> является однобайтовым (код символа &lt; 256).
    /// Необходимо для использования оптимизированного поиска индексов через массив фиксированного размера.
    /// </summary>
    /// <param name="s"> Проверяемая строка </param>
    /// <returns> True, если все символы однобайтовые; иначе false </returns>
    private static bool IsSingleByte(string s)
    {
        foreach (char c in s)
        {
            if (c > 255)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Валидирует алфавит дробных цифр.
    /// Требования: минимум 2 символа, строго возрастающий порядок кодов, все символы однобайтовые.
    /// </summary>
    /// <param name="digits"> Алфавит цифр </param>
    private static void ValidateDigits(string digits)
    {
        lock (ValidatedDigits)
        {
            if (ValidatedDigits.Contains(digits))
            {
                return;
            }
        }

        if (digits.Length < 2 || !IsStrictlyAscending(digits))
        {
            throw new ArgumentException($"Цифры должны содержать минимум 2 символа в строго возрастающем порядке кодов символов: {digits}");
        }
        if (!IsSingleByte(digits))
        {
            throw new ArgumentException($"Цифры должны быть однобайтовыми (код символа 0-255): {digits}");
        }

        lock (ValidatedDigits)
        {
            ValidatedDigits.Add(digits);
        }
    }

    /// <summary>
    /// Валидирует алфавит головок (intDigits).
    /// Требования: четное количество символов (минимум 2), строго возрастающий порядок кодов, все символы однобайтовые.
    /// </summary>
    /// <param name="intDigits"> Алфавит головок </param>
    private static void ValidateIntDigits(string intDigits)
    {
        lock (ValidatedIntDigits)
        {
            if (ValidatedIntDigits.Contains(intDigits))
            {
                return;
            }
        }

        if (intDigits.Length < 2 || intDigits.Length % 2 != 0 || !IsStrictlyAscending(intDigits))
        {
            throw new ArgumentException($"intDigits должно содержать четное количество символов (минимум 2) в строго возрастающем порядке кодов символов: {intDigits}");
        }
        if (!IsSingleByte(intDigits))
        {
            throw new ArgumentException($"intDigits должно быть однобайтовым (код символа 0-255): {intDigits}");
        }

        lock (ValidatedIntDigits)
        {
            ValidatedIntDigits.Add(intDigits);
        }
    }

    /// <summary>
    /// Генерирует ключ порядка, который лексикографически находится строго между `a` и `b`.
    /// </summary>
    /// <remarks>
    /// Если оба параметра null, генерируется начальный ключ.
    /// Если один из них null, генерируется ключ до или после другого.
    /// Порядок передачи <paramref name="a"/> и <paramref name="b"/> не важен, они будут автоматически упорядочены.
    /// </remarks>
    /// <param name="a"> Нижняя граница: существующий ключ порядка или null </param>
    /// <param name="b"> Верхняя граница: существующий ключ порядка или null </param>
    /// <param name="digits"> Алфавит цифр. По умолчанию <see cref="Base62Digits"/> </param>
    /// <param name="intDigits"> Алфавит головок. По умолчанию <see cref="Base52Digits"/> (если digits тоже по умолчанию) или <paramref name="digits"/> </param>
    /// <returns> Новый уникальный ключ порядка, находящийся между <paramref name="a"/> и <paramref name="b"/> </returns>
    public static string GenerateKeyBetween(
        string? a,
        string? b,
        string? digits = null,
        string? intDigits = null)
    {
        if (intDigits is not null)
        {
            ValidateIntDigits(intDigits);
        }
        else
        {
            intDigits = digits ?? Base52Digits;
        }

        if (digits is not null)
        {
            ValidateDigits(digits);
        }
        else
        {
            digits = Base62Digits;
        }

        var lookup = GetDigitIndex(digits);
        var intLookup = GetDigitIndex(intDigits);

        if (a != null)
        {
            ValidateOrderKey(a, digits, intDigits, intLookup);
        }
        if (b != null)
        {
            ValidateOrderKey(b, digits, intDigits, intLookup);
        }

        if (a != null && b != null)
        {
            // Меняем местами, если переданы в обратном порядке, чтобы гарантировать a < b
            if (string.CompareOrdinal(a, b) > 0)
            {
                (a, b) = (b, a);
            }
        }

        if (a == null)
        {
            if (b == null)
            {
                // Оба null: возвращаем самый простой положительный ключ.
                // Берем первую головку из второй половины intDigits (минимальная положительная длина).
                var head = intDigits[intDigits.Length / 2];
                return $"{head}{digits[0]}";
            }

            var ib = GetIntegerPart(b, intDigits, intLookup);
            var fb = b[ib.Length..];

            if (IsSmallestInteger(ib, digits, intDigits))
            {
                return ib + Midpoint("", fb, digits, lookup);
            }

            if (string.CompareOrdinal(ib, b) < 0)
            {
                return ib;
            }

            var res = DecrementInteger(ib, digits, lookup, intDigits, intLookup);
            return res ?? throw new InvalidOperationException("Невозможно декрементировать дальше (достигнут минимум).");
        }

        if (b == null)
        {
            var ia = GetIntegerPart(a, intDigits, intLookup);
            var fa = a[ia.Length..];
            var i = IncrementInteger(ia, digits, lookup, intDigits, intLookup);

            // Если инкремент невозможен (достигнут максимум), используем midpoint внутри текущей целой части
            return i ?? ia + Midpoint(fa, null, digits, lookup);
        }

        var iaFull = GetIntegerPart(a, intDigits, intLookup);
        var faFull = a[iaFull.Length..];
        var ibFull = GetIntegerPart(b, intDigits, intLookup);
        var fbFull = b[ibFull.Length..];

        if (iaFull == ibFull)
        {
            return iaFull + Midpoint(faFull, fbFull, digits, lookup);
        }

        var incRes = IncrementInteger(iaFull, digits, lookup, intDigits, intLookup) ?? throw new InvalidOperationException("Невозможно инкрементировать дальше (достигнут максимум).");
        if (string.CompareOrdinal(incRes, b) < 0)
        {
            return incRes;
        }

        return iaFull + Midpoint(faFull, null, digits, lookup);
    }

    /// <summary>
    /// Генерирует массив из <paramref name="n"/> различных ключей порядка, отсортированных между <paramref name="a"/> и <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// Использует рекурсивное деление пополам для эффективной генерации равномерного распределения ключей.
    /// </remarks>
    /// <param name="a"> Нижняя граница: существующий ключ порядка или null </param>
    /// <param name="b"> Верхняя граница: существующий ключ порядка или null </param>
    /// <param name="n"> Количество требуемых ключей. Должно быть >= 0 </param>
    /// <param name="digits"> Алфавит цифр. По умолчанию <see cref="Base62Digits"/> </param>
    /// <param name="intDigits"> Алфавит головок. По умолчанию <see cref="Base52Digits"/> (если digits тоже по умолчанию) или <paramref name="digits"/> </param>
    /// <returns> Список строк, содержащий ровно <paramref name="n"/> ключей, отсортированных по возрастанию </returns>
    public static List<string> GenerateNKeysBetween(
        string? a,
        string? b,
        int n,
        string? digits = null,
        string? intDigits = null)
    {
        if (intDigits is not null)
        {
            ValidateIntDigits(intDigits);
        }
        else
        {
            intDigits = digits ?? Base52Digits;
        }

        if (digits is not null)
        {
            ValidateDigits(digits);
        }
        else
        {
            digits = Base62Digits;
        }

        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n), $"n должен быть >= 0: {n}");
        }
        if (n == 0)
        {
            return [];
        }
        if (n == 1)
        {
            return [GenerateKeyBetween(a, b, digits, intDigits)];
        }

        if (b == null)
        {
            var c = GenerateKeyBetween(a, b, digits, intDigits);
            var result = new List<string>(n) { c };
            for (var i = 0; i < n - 1; i++)
            {
                c = GenerateKeyBetween(c, b, digits, intDigits);
                result.Add(c);
            }
            return result;
        }

        if (a == null)
        {
            var c = GenerateKeyBetween(a, b, digits, intDigits);
            var result = new List<string>(n) { c };
            for (var i = 0; i < n - 1; i++)
            {
                c = GenerateKeyBetween(a, c, digits, intDigits);
                result.Add(c);
            }
            result.Reverse();
            return result;
        }

        var mid = n / 2;
        var middleKey = GenerateKeyBetween(a, b, digits, intDigits);

        var leftKeys = GenerateNKeysBetween(a, middleKey, mid, digits, intDigits);
        var rightKeys = GenerateNKeysBetween(middleKey, b, n - mid - 1, digits, intDigits);

        var finalResult = new List<string>(n);
        finalResult.AddRange(leftKeys);
        finalResult.Add(middleKey);
        finalResult.AddRange(rightKeys);

        return finalResult;
    }
}
