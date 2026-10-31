using System.IO;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Integration;

/// <summary>Geospatial statistics survive a footer rewrite: thrift doubles (the bounding box) are written back.</summary>
public class GeospatialStatisticsAppendTest : TestBase {

    [Fact]
    public async Task Bounding_box_survives_an_append() {
        // data/geometry.parquet: ten WKB points (i, -i) written by pyarrow 26, with GeospatialStatistics
        using var file = new MemoryStream();
        using(Stream source = OpenTestFile("geometry.parquet"))
            await source.CopyToAsync(file);

        ParquetSchema schema;
        await using(ParquetReader before = await ParquetReader.CreateAsync(new MemoryStream(file.ToArray()))) {
            schema = before.Schema;
            Assert.Equal(9, before.Metadata!.RowGroups[0].Columns[0].MetaData!.GeospatialStatistics!.Bbox!.Xmax);
        }

        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, file, append: true)) {
        }

        file.Position = 0;
        await using ParquetReader after = await ParquetReader.CreateAsync(file);
        BoundingBox box = after.Metadata!.RowGroups[0].Columns[0].MetaData!.GeospatialStatistics!.Bbox!;
        Assert.Equal((0d, 9d, -9d, 0d), (box.Xmin, box.Xmax, box.Ymin, box.Ymax));
    }
}
