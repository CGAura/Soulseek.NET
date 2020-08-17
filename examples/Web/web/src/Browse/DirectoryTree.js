import React from 'react';

import { 
  List
} from 'semantic-ui-react';

const subtree = (root, selectedDirectoryName, onSelect) => {
  return (root || []).map((d, index) => {
      const selected = d.directoryName === selectedDirectoryName;
      return (
        <List key={index} className='browse-folderlist-list'>
          <List.Item style={{opacity: d.locked ? 0.5 : 1}}>
              <List.Icon 
                className='browse-folderlist-icon'
                name={d.locked === true ? 'lock' : selected ? 'folder open' : 'folder'}
                color={selected ? 'blue' : 'black'}
              />
              <List.Content>
                  <List.Header 
                    className='browse-folderlist-header'
                    onClick={(event) => onSelect(event, d)}
                    style={{ color: selected ? '#0E6EB8' : 'black', fontWeight: selected ? 700 : 400 }}
                  >
                      {d.directoryName.split('\\').pop().split('/').pop()}
                  </List.Header>
                  <List.List>
                      {subtree(d.children, selectedDirectoryName, onSelect)}
                  </List.List>
              </List.Content>
          </List.Item>
      </List>)
    })
}

const DirectoryTree = ({ tree, selectedDirectoryName, onSelect }) => subtree(tree, selectedDirectoryName, onSelect);

export default DirectoryTree;