using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;

namespace DivisionEngine;

/// <summary>
///     Unordered collection implementation which is fast in adding, removing and enumeration.
/// </summary>
/// <typeparam name="T"></typeparam>
public class SparseSet<T> : IReadOnlyCollection<T>, IDisposable
{
    private int _capacity;
    private int _count;
    private ElementData[] _elementData;
    private T[] _elements;
    private bool _isDisposed;
    private int _rootEmpty;
    private SparseElement[] _sparseElements;

    private ulong _sparseVersion = 1;
    private bool _sparseVersionTaken;
    private ulong _version = 1;
    private bool _versionTaken;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SparseSet(int capacity = 4)
    {
        if (capacity <= 0) throw new ArgumentException("Capacity must be greater than 0.");
        _capacity = capacity;

        _elementData = ArrayPool<ElementData>.Shared.Rent(capacity);
        _elements = ArrayPool<T>.Shared.Rent(capacity);
        _sparseElements = ArrayPool<SparseElement>.Shared.Rent(capacity);

        Array.Clear(_elementData);
        Array.Clear(_elements);
        Array.Clear(_sparseElements);
    }

    private int Size
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            return _elements.Length;
        }
    }

    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            return _capacity;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            ThrowIfDisposed();

            if (value < _capacity)
                throw new NotSupportedException("SparseSet cannot shrink.");

            if (value == _capacity) return;

            if (value > Size)
            {
                var newElementData = ArrayPool<ElementData>.Shared.Rent(value);
                var newSparseElements = ArrayPool<SparseElement>.Shared.Rent(value);
                var newElements = ArrayPool<T>.Shared.Rent(value);

                Array.Copy(_elementData, newElementData, _capacity);
                Array.Copy(_sparseElements, newSparseElements, _capacity);
                Array.Copy(_elements, newElements, _capacity);

                ArrayPool<ElementData>.Shared.Return(_elementData);
                ArrayPool<SparseElement>.Shared.Return(_sparseElements);
                ArrayPool<T>.Shared.Return(_elements);

                _elementData = newElementData;
                _sparseElements = newSparseElements;
                _elements = newElements;
            }

            Array.Clear(_elementData, _capacity, value - _capacity);
            Array.Clear(_sparseElements, _capacity, value - _capacity);
            Array.Clear(_elements, _capacity, value - _capacity);

            _capacity = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DisposeCore();
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            return _count;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private set
        {
            ThrowIfDisposed();
            _count = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IEnumerator<T> GetEnumerator()
    {
        ThrowIfDisposed();
        _versionTaken = true;
        return new Enumerator(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    IEnumerator IEnumerable.GetEnumerator()
    {
        ThrowIfDisposed();
        return GetEnumerator();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    /// <summary>
    ///     Gets the ReadOnlySpan for enumeration.
    ///     The contents of returned ReadOnlySpan may be invalid if the SparseSet is modified after this call.
    /// </summary>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpanUnsafe()
    {
        ThrowIfDisposed();
        return _elements.AsSpan()[..Count];
    }

    /// <summary>
    ///     Gets a reference to specific element.
    ///     Returned reference may be invalid if the SparseSet is modified after this call.
    /// </summary>
    /// <param name="handle"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T AsRefUnsafe(ElementHandle handle)
    {
        ThrowIfDisposed();
        var sparseIndex = handle._sparseIndex;
        ref var sparseElement = ref _sparseElements[sparseIndex];

        if (sparseElement._version != handle._elemntVersion)
            throw new InvalidOperationException(
                $"This element is updated. sparseIndex: {sparseIndex}, sparseElementVersion expected: {handle._elemntVersion} but actual {sparseElement._version}, count: {_count}, capacity: {_capacity}, size: {Size}, rootEmpty: {_rootEmpty}");
        if (_sparseVersion != handle._sparseVersion)
            throw new InvalidOperationException("This element is cleared.");

        return ref _elements[sparseElement._index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddSparseElement(int pointToIndex, out ElementHandle elementHandle)
    {
        ThrowIfDisposed();
        var sparseIndex = _rootEmpty;
        if (_capacity <= _rootEmpty) throw new InvalidOperationException("SparseSet is full.");

        ref var sparseElement = ref _sparseElements[sparseIndex];

        var nextEmpty = sparseElement.NextEmpty;
        if (nextEmpty == -1) nextEmpty = sparseIndex + 1;
        _rootEmpty = nextEmpty;

        var version = ++sparseElement._version;
        sparseElement._index = pointToIndex;
        sparseElement.NextEmpty = -1;

        elementHandle = new ElementHandle(this, sparseIndex, version, _sparseVersion);
        _sparseVersionTaken = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveSparseElement(in ElementHandle handle, out int index)
    {
        ThrowIfDisposed();
        var sparseIndex = handle._sparseIndex;
        ref var sparseElement = ref _sparseElements[sparseIndex];

        if (sparseElement._version != handle._elemntVersion)
            throw new InvalidOperationException(
                $"This element is updated. sparseIndex: {sparseIndex}, sparseElementVersion expected: {handle._elemntVersion} but actual {sparseElement._version}, count: {_count}, capacity: {_capacity}, size: {Size}, rootEmpty: {_rootEmpty}");
        if (_sparseVersion != handle._sparseVersion)
            throw new InvalidOperationException("This element is cleared.");

        index = sparseElement._index;

        sparseElement._version++;
        sparseElement._index = -1;
        sparseElement.NextEmpty = _rootEmpty;
        _rootEmpty = sparseIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T element, out ElementHandle handle)
    {
        ThrowIfDisposed();
        var capacity = _capacity;
        if (capacity <= Count)
        {
            // expand
            var msb = 0;
            for (var i = Count; i != 0; i >>= 1)
                msb++;
            msb = Math.Max(2, msb);
            Capacity = 1 << msb;
        }

        var index = Count++;
        AddSparseElement(index, out handle);

        _elementData[index] = new ElementData
        {
            _sparseIndex = handle._sparseIndex
        };
        _elements[index] = element;

        if (_versionTaken)
        {
            _version++;
            _versionTaken = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Remove(in ElementHandle handle)
    {
        ThrowIfDisposed();
        if (handle._instance != this) throw new InvalidOperationException();
        RemoveSparseElement(handle, out var index);

        var lastIndex = --Count;

        ref var lastElementData = ref _elementData[lastIndex];
        ref var lastElement = ref _elements[lastIndex];

        if (index != lastIndex)
        {
            ref var elementData = ref _elementData[index];
            ref var element = ref _elements[index];
            elementData = lastElementData;
            element = lastElement;
            _sparseElements[elementData._sparseIndex]._index = index;
        }

        _elements[lastIndex] = default;
        lastElementData._sparseIndex = -1;

        if (_versionTaken)
        {
            _version++;
            _versionTaken = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        ThrowIfDisposed();

        if (_sparseVersionTaken)
        {
            _sparseVersion++;
            _sparseVersionTaken = false;
        }

        if (_versionTaken)
        {
            _version++;
            _versionTaken = false;
        }

        _capacity = 0;
        _rootEmpty = 0;
        Count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DisposeCore()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        ArrayPool<ElementData>.Shared.Return(_elementData);
        ArrayPool<SparseElement>.Shared.Return(_sparseElements);
        ArrayPool<T>.Shared.Return(_elements);

        _elementData = null;
        _sparseElements = null;
        _elements = null;
    }

    ~SparseSet()
    {
        DisposeCore();
    }

    public readonly struct ElementHandle(
        SparseSet<T> instance,
        int sparseIndex,
        ulong elementVersion,
        ulong sparseVersion)
    {
        internal readonly SparseSet<T> _instance = instance;
        internal readonly int _sparseIndex = sparseIndex;
        internal readonly ulong _elemntVersion = elementVersion;
        internal readonly ulong _sparseVersion = sparseVersion;
    }

    private struct SparseElement
    {
        public int _index;

        // zero represents a sparse element next to this sparse element
        public int NextEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _nextEmptyPlusOne - 1;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _nextEmptyPlusOne = value + 1;
        }

        private int _nextEmptyPlusOne;

        public ulong _version;
    }

    private struct ElementData
    {
        public int _sparseIndex;
    }

    private struct Enumerator(SparseSet<T> target) : IEnumerator<T>
    {
        private readonly ulong _version = target._version;
        private int _index = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssertVersion()
        {
            if (target._version != _version)
                throw new InvalidOperationException(
                    "Collection was modified; enumeration operation may not execute.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            AssertVersion();
            _index++;
            return _index < target.Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            AssertVersion();
            _index = 0;
        }

        public T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                AssertVersion();
                return target._elements[_index];
            }
        }

        object IEnumerator.Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Current;
        }
    }
}