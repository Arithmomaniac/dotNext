Base Class Library Enhancements
====

# Randomization
Related class: [RandomExtensions](xref:DotNext.RandomExtensions)

Extension methods for random data generation extends both classes [Random](https://docs.microsoft.com/en-us/dotnet/api/system.random) and [RandomNumberGenerator](https://docs.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator).

## Random string generation
Provides a way to generate random string of the given length and set of allowed characters.
```csharp
using DotNext;
using System;

var rand = new Random();
var password = rand.NextString("abc123", 10);   
//now password has 10 characters
//each character is equal to 'a', 'b', 'c', '1', '2' or '3'
```

The same extension method is provided for class [RandomNumberGenerator](https://docs.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator).

## Random boolean generation
Provides a way to generate boolean value with the given probability
```csharp
using DotNext;

var rand = new Random();
var b = rand.NextBoolean(0.3D); //0.3 is a probability of TRUE value
```

The same extension method is provided for class [RandomNumberGenerator](https://docs.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator).

# String extensions
Related class: [StringExtensions](xref:DotNext.StringExtensions).

## Reverse string
Extension method _Reverse_ allows to reverse string characters and returns a new string:
```csharp
using DotNext;

var str = "abc".Reverse(); //str is "cba"
```

## Trim by length
Extension method _TrimLength_ limits the length of string or span:
```csharp
using DotNext;

var str = "abc".TrimLength(2);  //str is "ab"
str = "abc".TrimLength(4);  //str is "abc"

Span<int> array = new int[] { 10, 20, 30 };
array = array.TrimLength(2);    //array is { 10, 20 }
```

# Delegates
Related classes: [DelegateHelpers](xref:DotNext.DelegateHelpers), [Func](xref:DotNext.Func), [Converter](xref:DotNext.Converter), [Predicate](xref:DotNext.Predicate).

## Change type of delegate
Different types of delegates can refer to the same method. For instance, `Func<string, string>` represents the same signature as `Converter<string, string>`. It means that the delegate instance can be converted into another delegate type if signature matches.
```csharp
using DotNext;

Func<string, int> lengthOf = str => str.Length;
Converter<string, int> lengthOf2 = lengthOf.ChangeType<Converter<string, int>>();
```

## Specialized delegate converters
Conversion between mostly used delegate types: [Predicate&lt;T&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.predicate-1), [Func&lt;T, TResult&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.func-2) and [Converter&lt;TInput, TOutput&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.converter-2).

```csharp
using DotNext;

Predicate<string> isEmpty = str => str is { Length: 0 };
Func<string, bool> isEmptyFunc = isEmpty.AsFunc();
Converter<string, bool> isEmptyConv = isEmpty.AsConverter();
```

## Predefined delegates
Cached delegate instances for mostly used delegate types: [Predicate&lt;T&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.predicate-1), [Func&lt;T, TResult&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.func-2) and [Converter&lt;TInput, TOutput&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.converter-2).

```csharp
using DotNext;

Func<int, int> identity = Func.Identity<int>(); //identity delegate which returns input argument without any changes
Predicate<bool> truePredicate = Predicate.Constant<bool>(true); // predicate which always returns true
Predicate<bool> falsePredicate = Predicate.Constant<bool>(false); //predicate which always returns false
Predicate<string> nullCheck = Predicate.IsNull<string>(); //predicate checking whether the input argument is null
Predicate<string> notNullCheck = Predicate.IsNotNull<string>(); //predicate checking whether the input argument is not null
```

## Logical operators for Predicate
Extension methods implementation logical operators for Predicate delegate instances:

```csharp
using DotNext;

Predicate<string> predicate = str => str.Length == 0;
predicate = predicate.Negate();
predicate = predicate.And(str => str.Length > 2);
predicate = predicate.Or(str => str.Length % 2 == 0);
predicate = predicate.Xor(Predicate.IsNull<string>());
```

## Delegate Factories
C# 9 introduces typed function pointers. However, conversion between regular delegates and function pointers is not supported. `DelegateHelpers` offers factory methods allowing creation of delegate instances from function pointers. These factory methods support implicit capturing of the first argument as well:
```csharp
using DotNext;

static int GetHashCode(string s)
{
}

delegate*<string, int> hashCode = &GetHashCode;
Func<string, int> openDelegate = DelegateHelpers.CreateDelegate<string, int>(hashCode);
Func<int> closedDelegate = DelegateHelpers.CreateDelegate<string, int>(hashCode, "Hello, world!");
```

## Range check
Checks whether the given value is in specific range.
```csharp
using DotNext;

var b = 10.IsBetween(5.Enclosed(), 11.Enclosed()); //b == true
b = 10.IsBetween(0.Disclosed(), 4.Disclosed()); //b == false
```

# Equality check
Related classes: [BasicExtensions](xref:DotNext.BasicExtensions).

Extension method _IsOneOf_ allows to check whether the value is equal to one of the given values.

```csharp
using DotNext;

var b = 42.IsOneOf([0, 5, 42, 3]); //b == true

b = "a".IsOneOf(["b", "c", "d"]); //b == false
```

## Equality check
_BitwiseEquals_ extension method performs bitwise equality between two regions of memory referenced by the arrays. Element type of these arrays should be of unmanaged value type, e.g. `int`, `long`, `System.Guid`.

```csharp
var array2 = new int[] { 1, 2, 3 };
Span.BitwiseEquals(new [] {1, 2, 4}, array2);    //false
```

# Timestamp
[Timestamp](xref:DotNext.Diagnostics.Timestamp) value type can be used as allocation-free alternative to [Stopwatch](https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.stopwatch) when you need to measure time intervals.

```csharp
using DotNext.Diagnostics;
using System;

var timestamp = new Timestamp();
//long-running operation
Console.WriteLine(timestamp.Elapsed);
```

`Elapsed` property returning value of [TimeSpan](https://docs.microsoft.com/en-us/dotnet/api/system.timespan) type which indicates the difference between `timestamp` and the current point in time.

This type should not be used as a unique identifier of some point in time. The created time stamp may identify the time since the start of the process, OS, user session or whatever else.

# Dynamic Task Result
In .NET it is not possible to obtain a result from [task](https://docs.microsoft.com/en-us/dotnet/api/system.threading.tasks.task-1) if its result type is not known at compile-time. It can be useful if you are writing proxy or SOAP Middleware using ASP.NET Core and task type is not known for your code. .NEXT provides two ways of doing that:
1. Synchronous method `GetResult` which is located in [Synchronization](xref:DotNext.Threading.Tasks.Synchronization) class
1. Asynchronous method `AsDynamic` which is located in [Conversion](xref:DotNext.Threading.Tasks.Conversion) class.

All you need is to have instance of non-generic [Task](https://docs.microsoft.com/en-us/dotnet/api/system.threading.tasks.task) class because all types tasks derive from it.

Under the hood, .NEXT uses Dynamic Language Runtime feature in combination with high-speed optimizations.

The following example demonstrates this approach:
```csharp
using DotNext.Threading.Tasks;
using System.Threading.Tasks;

//assume that t is of unknown Task<T> type
Task t = Task.FromResult("Hello, world!");

//obtain result synchronously
Result<dynamic> result = t.GetResult(CancellationToken.None);

//obtain result asynchronously
dynamic result = await t.AsDynamic();
```

# Hex Converter
[Convert.ToHexString](https://docs.microsoft.com/en-us/dotnet/api/system.convert.tohexstring) method from .NET standard library allow to convert array of bytes to its hexadecimal representation. However, it doesn't support `Span<char>` as a destination buffer. Moreover, the method produces UTF-16 encoded hex characters only.

[Hex](xref:DotNext.Buffers.Text.Hex) static class exposes static methods for fast hexadecimal conversion:
* `EncodeToUtf16` allows to convert `ReadOnlySpan<byte>` to hexadecimal representation and places result to `Span<char>`
* `EncodeToUtf8` allows to convert `ReadOnlySpan<byte>` to hexadecimal representation and places result to `Span<byte>`
* `DecodeFromUtf16` allows to convert hexadecimal UTF-16 encoded string in the from of `ReadOnlySpan<char>` to bytes and places result to `Span<byte>`
* `DecodeFromUtf8` allows to convert hexadecimal UTF-8 encoded string in the from of `ReadOnlySpan<byte>` to bytes and places result to `Span<byte>`

```csharp
using DotNext.Buffers.Text;

ReadOnlySpan<byte> bytes = stackalloc byte[] {8, 16, 24};
Span<char> hex = stackalloc char[bytes.Length * 2];
Hex.EncodeToUtf16(bytes, hex); //now hex == 081018
```

# Polling of Concurrent Collections
[IProducerConsumerCollection&lt;T&gt;](https://docs.microsoft.com/en-us/dotnet/api/system.collections.concurrent.iproducerconsumercollection-1) is a common interface for concurrent collections in .NET library. Consumer of such collection uses `TryTake` or more specialized method provided by subclasses to obtain elements from the collection. For convenience, [Collection](xref:DotNext.Collections.Generic.Collection) static class offers `GetConsumer` extension method to obtain consuming enumerable collection over the elements in the concurrent collection so you can use classic **foreach** loop:
```csharp
using DotNext.Collections.Concurrent;
using System.Collections.Concurrent;

var queue = new ConcurrentQueue<int>();
foreach (int item in queue.GetConsumer())
{
    // collection enumerator will call .TryDequeue(out int result) for each iteration of this loop
}
```