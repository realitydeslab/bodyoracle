import os
import random
import shutil

# Define paths
image_dir = './img'  # Folder containing all images
label_dir = './lab'  # Folder containing all labels
output_dir = './'

# Define split ratios
train_ratio = 0.7  # 70% for training
val_ratio = 0.2    # 20% for validation
test_ratio = 0.1   # 10% for testing

# Get list of all image files
image_files = [f for f in os.listdir(image_dir) if not f.startswith('.')]
random.shuffle(image_files)

# Calculate split sizes
num_files = len(image_files)
num_train = int(num_files * train_ratio)
num_val = int(num_files * val_ratio)
num_test = num_files - num_train - num_val

# Split the files
train_files = image_files[:num_train]
val_files = image_files[num_train:num_train + num_val]
test_files = image_files[num_train + num_val:]

# Function to move files
def move_files(files, split):
    for file in files:
        shutil.copyfile(
            os.path.join(image_dir, file),
            os.path.join(output_dir, 'images', split, file)
        )
        
        label_file = os.path.splitext(file)[0] + '.txt'
        # tmp = label_file
        # if file.count('_new') > 0:
        #     tmp = file.split('_new')[0] + ' Medium_new.txt'
        shutil.copyfile(
            os.path.join(label_dir, label_file),
            os.path.join(output_dir, 'labels', split, label_file)
        )
        

# Move files to respective folders
move_files(train_files, 'train')
move_files(val_files, 'valid')
move_files(test_files, 'test')