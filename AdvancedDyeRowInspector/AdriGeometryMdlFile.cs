using System;
using System.IO;

using Lumina.Data.Files;
using Lumina.Data.Parsing;

namespace AdvancedDyeRowInspector;

// Reads only the model records necessary for geometry
// inspection. Does not parse bone tables, shape values,
// bone maps or bounding-box records.

public sealed class AdriGeometryMdlFile : MdlFile
{
    private void RequireBytes(long count, string section)
    {
        long position = Reader.BaseStream.Position;
        long length = Reader.BaseStream.Length;

        if (count < 0 ||
            position < 0 ||
            position > length ||
            count > length - position)
        {
            throw new InvalidDataException(
                $"ADRI MDL parser: invalid {section} range " +
                $"at {position}, need {count}, length {length}");
        }
    }

    public override void LoadFile()
    {
        bool previousEndian = Reader.IsLittleEndian;

        try
        {
            RequireBytes(68, "file header");

            Reader.IsLittleEndian = BitConverter.IsLittleEndian;
            FileHeader = MdlStructs.ModelFileHeader.Read(Reader);
        }
        finally
        {
            Reader.IsLittleEndian = previousEndian;
        }

        if (FileHeader.VertexDeclarationCount > 2048 ||
            FileHeader.MaterialCount > 4096)
        {
            throw new InvalidDataException(
                "Unreasonable model header counts");
        }

        VertexDeclarations =
            new MdlStructs.VertexDeclarationStruct[
                FileHeader.VertexDeclarationCount];

        for (int i = 0;
             i < VertexDeclarations.Length;
             i++)
        {
            RequireBytes(136, "vertex declaration");

            VertexDeclarations[i] =
                MdlStructs.VertexDeclarationStruct.Read(Reader);
        }

        RequireBytes(8, "string table header");

        StringCount = Reader.ReadUInt16();
        Reader.ReadUInt16();

        uint stringSize = Reader.ReadUInt32();

        if (stringSize > 8 * 1024 * 1024)
        {
            throw new InvalidDataException(
                "Oversized model string table");
        }

        RequireBytes(stringSize, "string table");

        Strings = Reader.ReadBytes(
            checked((int)stringSize));

        ModelHeader = Reader.ReadStructure<MdlStructs.ModelHeader>();

        if (ModelHeader.MeshCount > 4096 ||
            ModelHeader.MaterialCount > 4096 ||
            ModelHeader.SubmeshCount > 60000 ||
            ModelHeader.AttributeCount > 60000 ||
            ModelHeader.ElementIdCount > 16000)
        {
            throw new InvalidDataException(
                "Unsupported model record counts");
        }

        ElementIds = new MdlStructs.ElementIdStruct[
            ModelHeader.ElementIdCount];

        for (int i = 0; i < ElementIds.Length; i++)
        {
            ElementIds[i] =
                MdlStructs.ElementIdStruct.Read(Reader);
        }

        Lods = Reader.ReadStructuresAsArray<
            MdlStructs.LodStruct>(3);

        if (ModelHeader.ExtraLodEnabled)
        {
            ExtraLods = Reader.ReadStructuresAsArray<
                MdlStructs.ExtraLodStruct>(3);
        }

        Meshes = new MdlStructs.MeshStruct[
            ModelHeader.MeshCount];

        for (int i = 0; i < Meshes.Length; i++)
        {
            Meshes[i] = MdlStructs.MeshStruct.Read(Reader);
        }

        AttributeNameOffsets = Reader.ReadUInt32Array(
            ModelHeader.AttributeCount);

        TerrainShadowMeshes = Reader.ReadStructuresAsArray<
            MdlStructs.TerrainShadowMeshStruct>(
                ModelHeader.TerrainShadowMeshCount);

        Submeshes = Reader.ReadStructuresAsArray<
            MdlStructs.SubmeshStruct>(
                ModelHeader.SubmeshCount);

        TerrainShadowSubmeshes = Reader.ReadStructuresAsArray<
            MdlStructs.TerrainShadowSubmeshStruct>(
                ModelHeader.TerrainShadowSubmeshCount);

        MaterialNameOffsets = Reader.ReadUInt32Array(
            ModelHeader.MaterialCount);

        // Stop before unrelated later model records.
    }
}