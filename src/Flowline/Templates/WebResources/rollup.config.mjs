import typescript from '@rollup/plugin-typescript'
import eslint from '@rollup/plugin-eslint';
import fs from 'fs';
import * as changeCase from "change-case";

// settings
const namespacePrefix = ''; // like 'MyCompany.' (don't forget the dot at the end!)
const fileHeader =
`/**
  * This code is generated using Rollup (https://rollupjs.org/) and TypeScript (https://www.typescriptlang.org/).
  * Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.
  */`;

const plugins = [eslint(), typescript()];

// generate a bundle statement for each TypeScript and JavaScript file in 'src/', but not its subdirectories
export default fs.readdirSync('src/', { withFileTypes: true })
    .filter(e => e.isFile() && (e.name.endsWith('.ts') || e.name.endsWith('.js')))
    .map(file => ({
        input: `src/${file.name}`,
        output: {
            name: `${namespacePrefix}${changeCase.pascalCase(file.name.replace(/\.[^/.]+$/, ""))}`,
            dir: 'dist',
            format: 'iife',
            extend: true,
            sourcemap: false,
            banner: fileHeader
        },
        plugins
    }));
