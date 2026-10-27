# Genesis.FractionalIndexing

[![stable](https://img.shields.io/nuget/v/Genesis.FractionalIndexing.svg?label=stable)](https://www.nuget.org/packages/Genesis.FractionalIndexing/)

**Genesis.FractionalIndexing** — это высокопроизводительная реализация алгоритма дробного индексирования (fractional indexing) для .NET 10+.

Библиотека позволяет вставлять элементы в упорядоченную последовательность между двумя существующими элементами без необходимости пересортировки всего списка или изменения индексов соседей. Это идеально подходит для задач drag-and-drop списков, древовидных структур и систем управления контентом, где порядок важен, но данные динамичны.

Данный порт основан на оригинальной реализации [rocicorp/fractional-indexing](https://github.com/rocicorp/fractional-indexing), которая, в свою очередь, базируется на статье Дэвида Гринспана *[Implementing Fractional Indexing](https://observablehq.com/@dgreensp/implementing-fractional-indexing)*.

## Особенности

*   **Без переиндексации:** Вставка нового элемента не требует обновления остальных записей в базе данных или памяти.
*   **Гибкие алфавиты:** Поддержка пользовательских наборов символов (`digits`) и маркеров длины целой части (`intDigits`). По умолчанию используется Base62.
*   **Детерминированность:** Алгоритм гарантирует стабильный лексикографический порядок сортировки строк.

## Установка

Через NuGet:

```bash
dotnet add package Genesis.FractionalIndexing
```

Или через Package Manager Console:

```powershell
Install-Package Genesis.FractionalIndexing
```

## Быстрый старт

Основной метод — `FractionalIndexing.GenerateKeyBetween`. Он принимает два параметра: нижнюю границу (`a`) и верхнюю границу (`b`). Любое из них может быть `null`, что означает «начало» или «конец» списка соответственно.

### Базовые операции

```csharp
using Genesis;

// 1. Создание первого элемента (пустой список)
// Результат: "a0"
string first = FractionalIndexing.GenerateKeyBetween(null, null);

// 2. Добавление после первого элемента
// Результат: "a1"
string second = FractionalIndexing.GenerateKeyBetween(first, null);

// 3. Добавление перед первым элементом
// Результат: "Zz" (лексикографически меньше "a0")
string zeroth = FractionalIndexing.GenerateKeyBetween(null, first);

// 4. Вставка между вторым ("a1") и третьим ("a2")
// Сначала создадим третий
string third = FractionalIndexing.GenerateKeyBetween(second, null); // "a2"

// Теперь вставим между ними
// Результат: "a1V" (находится строго между "a1" и "a2")
string middle = FractionalIndexing.GenerateKeyBetween(second, third);
```

### Массовая генерация ключей

Для эффективного заполнения диапазона используйте `GenerateNKeysBetween`. Это распределяет новые ключи равномерно, избегая чрезмерного роста их длины.

```csharp
// Генерируем 5 ключей в начале списка
List<string> initialBatch = FractionalIndexing.GenerateNKeysBetween(null, null, 5);
// Результат: ["a0", "a1", "a2", "a3", "a4"]

// Вставляем 3 новых элемента между "a1" и "a2"
var existingA = initialBatch[1]; // "a1"
var existingB = initialBatch[2]; // "a2"

List<string> insertedBatch = FractionalIndexing.GenerateNKeysBetween(existingA, existingB, 3);
// Результат будет содержать короткие ключи, лежащие строго между "a1" и "a2"
```

## Параметры настройки

Методы принимают необязательные параметры `digits` и `intDigits`:

*   **`digits`**: Алфавит для цифровых значений ключа. По умолчанию `Base62Digits` (`0-9A-Za-z`).
    *   *Требование:* Строка должна содержать минимум 2 символа, быть отсортированной по возрастанию кодов символов и состоять только из однобайтовых символов (код < 256).
*   **`intDigits`**: Алфавит для маркеров длины целой части (головки ключа). По умолчанию равен `digits`.
    *   *Требование:* Четное количество символов, отсортировано по возрастанию. Первая половина отвечает за отрицательные длины, вторая — за положительные.

### Пример: Использование Base10

Если вам нужны чисто числовые идентификаторы (без букв), вы можете передать `"0123456789"` как `digits`. В этом случае `intDigits` также будет Base10 (self-headed), и ключи будут выглядеть как `"50"`, `"51"`, `"600"`.

```csharp
const string base10 = "0123456789";

// Создать первый элемент в Base10
// Результат: "50"
string key1 = FractionalIndexing.GenerateKeyBetween(null, null, digits: base10);

// Создать следующий
// Результат: "51"
string key2 = FractionalIndexing.GenerateKeyBetween(key1, null, digits: base10);
```

## Важные замечания

1.  **Лексикографическая сортировка:** Сгенерированные ключи предназначены для сравнения операторами `<`, `>` или методом `String.CompareOrdinal`. **Не используйте** культурно-зависимое сравнение (`StringComparer.CurrentCulture` или `localCompare` в JS), так как оно игнорирует регистр и может нарушить порядок.
2.  **Уникальность:** Алгоритм гарантирует уникальность при последовательной генерации. Однако при конкурентной записи (несколько процессов пишут одновременно) возможна коллизия, если они получат одинаковые границы. Для распределенных систем рекомендуется добавлять случайный джиттер (jitter) к конечным ключам или использовать транзакции базы данных.
3.  **Производительность:** Операции выполняются за время O(L), где L — длина ключа. Длина ключей растет logarithmically при множественных вставках в один и тот же интервал. Метод `GenerateNKeysBetween` помогает поддерживать компактность ключей.
