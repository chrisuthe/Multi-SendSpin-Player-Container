namespace MultiRoomAudio.Services.Logging;

/// <summary>
/// A simple fixed-size circular buffer.
/// </summary>
public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _start;
    private int _count;

    public CircularBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new T[capacity];
        _start = 0;
        _count = 0;
    }

    public int Count => _count;

    public void Add(T item)
    {
        var index = (_start + _count) % _capacity;
        _buffer[index] = item;

        if (_count < _capacity)
        {
            _count++;
        }
        else
        {
            // Buffer is full, overwrite oldest
            _start = (_start + 1) % _capacity;
        }
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
        Array.Clear(_buffer);
    }

    public IEnumerable<T> AsEnumerable()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return _buffer[(_start + i) % _capacity];
        }
    }

    public List<T> ToList()
    {
        var list = new List<T>(_count);
        for (int i = 0; i < _count; i++)
        {
            list.Add(_buffer[(_start + i) % _capacity]);
        }
        return list;
    }
}
