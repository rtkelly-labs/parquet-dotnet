using System;
using Parquet.Extensions;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Coverage;

/// <summary>
/// The internal column buffers refuse to hand out what they do not hold, and the schema element helpers answer false
/// for a missing element.
/// </summary>
public class ColumnBuffersTest {

    static DataField Attached<T>(DataField field) {
        _ = new ParquetSchema(field);
        return field;
    }

    [Fact]
    public void Reading_column_without_level_buffers_refuses_levels() {
        DataField field = Attached<int>(new DataField<int>("x"));
        using var rc = new ReadingColumn<int>(field, new int[4], null, null);
        Assert.Throws<InvalidOperationException>(() => rc.DefinitionLevelsToReadInto.Length);
        Assert.Throws<InvalidOperationException>(() => rc.RepetitionLevelsToReadInto.Length);
        Assert.Throws<InvalidOperationException>(() => rc.MarkDefinitionLevels(1, 1));
        Assert.Throws<InvalidOperationException>(() => rc.MarkRepetitionLevels(1));
        rc.AllocateDictionary(2);
        Assert.True(rc.HasDictionary);
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => rc.AllocateDictionary(2));
        Assert.Contains("already allocated", e.Message);
    }

    [Fact]
    public void Writing_column_refuses_parts_it_does_not_have() {
        DataField field = Attached<int>(new DataField<int>("x"));
        using WritingColumn<int> wc = WritingColumn<int>.NewWritingColumn(field, new[] { 1, 2 }, null);
        Assert.Throws<InvalidOperationException>(() => wc.Dictionary.Length);
        Assert.Throws<InvalidOperationException>(() => wc.DictionaryIndexes.Length);
        Assert.Throws<InvalidOperationException>(() => wc.DefinitionLevels.Length);
        Assert.Throws<InvalidOperationException>(() => wc.RepetitionLevels.Length);
        Assert.Equal(2, wc.CalculateRowCount());
    }

    [Fact]
    public void Writing_column_needs_its_field_and_a_schema() {
        Assert.Throws<ArgumentNullException>(() => WritingColumn<int>.NewWritingColumn(null!, new[] { 1 }, null));
        Assert.Throws<ArgumentException>(() => WritingColumn<int>.NewWritingColumn(new DataField<int>("loose"), new[] { 1 }, null));
    }

    [Fact]
    public void Schema_element_helpers_answer_false_for_no_element() {
        SchemaElement? none = null;
        Assert.False(none.IsList());
        Assert.False(none.IsMap());
        Assert.False(none.IsDecimal());
        Assert.False(none.IsString());
        Assert.False(none.IsDate());
        Assert.False(none.IsTimestampMillis());
        Assert.False(none.IsTimestampMicros());
        Assert.True(new SchemaElement { ConvertedType = ConvertedType.MAP_KEY_VALUE }.IsMap());
        Assert.True(new SchemaElement { ConvertedType = ConvertedType.ENUM }.IsString());
    }

    [Fact]
    public void Exceptions_carry_their_message_and_cause() {
        var inner = new InvalidOperationException("cause");
        Assert.Same(inner, new ParquetException("outer", inner).InnerException);
        Assert.NotNull(new ParquetException().Message);
        Assert.Equal("bad", new Meta.Proto.ThriftProtocolException("bad").Message);
        Assert.Same(inner, new Meta.Proto.ThriftProtocolException("bad", inner).InnerException);
    }
}
